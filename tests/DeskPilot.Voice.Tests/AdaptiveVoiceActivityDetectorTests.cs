using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class AdaptiveVoiceActivityDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(700)]
    [InlineData(1_500)]
    public async Task CaptureAsync_CommandAfterWakePause_PreservesSpeechAndEndpoints(int pauseMs)
    {
        await using var cursor = TestCursor.FromPcm(
            48_000,
            TestPcm.Concat(
                TestPcm.Silence(pauseMs, 0.01),
                TestPcm.Speech(600, 0.20),
                TestPcm.Silence(1_200, 0.01)));
        var detector = new AdaptiveVoiceActivityDetector();
        var progress = new List<VoiceActivityProgress>();

        var result = await detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Audio.Should().NotBeNull();
        progress.Should().ContainSingle();
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(48_000 + Samples(pauseMs));
        result.Diagnostics.SpeechEndSampleOffset.Should().Be(48_000 + Samples(pauseMs + 600));
        result.Diagnostics.ObservedDuration.Should().BeGreaterThanOrEqualTo(
            result.Diagnostics.CapturedDuration);
        double.IsFinite(result.Diagnostics.NoiseFloorRms).Should().BeTrue();
        result.Diagnostics.NoiseFloorRms.Should().BeGreaterThanOrEqualTo(0);
        double.IsFinite(result.Diagnostics.PeakRms).Should().BeTrue();
        result.Diagnostics.PeakRms.Should().BeInRange(0, 1);
        cursor.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_PreRoll_IsExactAndNeverPrecedesCursorStart()
    {
        var leading = TestPcm.Silence(100, 0.01);
        var speech = TestPcm.Speech(160, 0.20);
        var trailing = TestPcm.Silence(1_200, 0.01);
        await using var cursor = TestCursor.FromPcm(80_000, TestPcm.Concat(leading, speech, trailing));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Audio!.Pcm16.Span[..leading.Length].SequenceEqual(leading).Should().BeTrue();
        result.Audio.Pcm16.Length.Should().Be(Samples(100 + 160 + 1_200) * sizeof(short));
        result.Diagnostics.CapturedDuration.Should().Be(TimeSpan.FromMilliseconds(1_460));
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(81_600);
    }

    [Fact]
    public async Task CaptureAsync_PreRoll_PreservesOnlyLastThreeHundredMilliseconds()
    {
        var discarded = TestPcm.Silence(100, 0.005);
        var retained = TestPcm.Silence(300, 0.01);
        var speech = TestPcm.Speech(160, 0.20);
        var trailing = TestPcm.Silence(1_200, 0.01);
        await using var cursor = TestCursor.FromPcm(
            12_345,
            TestPcm.Concat(discarded, retained, speech, trailing));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Audio!.Pcm16.Span[..retained.Length].SequenceEqual(retained).Should().BeTrue();
        result.Audio.Pcm16.Length.Should().Be(Samples(300 + 160 + 1_200) * sizeof(short));
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(12_345 + Samples(400));
    }

    [Theory]
    [InlineData(149, false)]
    [InlineData(150, true)]
    public async Task CaptureAsync_ConfirmationRoundsUpToCompleteAnalysisFrames(
        int speechMs,
        bool expected)
    {
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(TestPcm.Speech(speechMs, 0.04), TestPcm.Silence(1_211, 0)));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.SpeechDetected.Should().Be(expected);
    }

    [Fact]
    public async Task CaptureAsync_InitialSilenceTimeout_ReturnsNoSpeechWithoutProgress()
    {
        await using var cursor = TestCursor.FromPcm(7_000, TestPcm.Silence(5_000, 0.01));
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeFalse();
        result.Audio.Should().BeNull();
        result.Duration.Should().Be(TimeSpan.FromSeconds(4));
        result.Diagnostics.ObservedDuration.Should().Be(TimeSpan.FromSeconds(4));
        result.Diagnostics.CapturedDuration.Should().Be(TimeSpan.Zero);
        progress.Should().BeEmpty();
        cursor.ReadFrameCount.Should().Be(200);
    }

    [Fact]
    public async Task CaptureAsync_OnePointOneNineSecondsOfSilence_DoesNotEndpoint()
    {
        var firstSpeech = TestPcm.Speech(200, 0.20);
        var pause = TestPcm.Silence(1_190, 0);
        var secondSpeech = TestPcm.Speech(210, 0.20);
        var ending = TestPcm.Silence(1_200, 0);
        await using var cursor = TestCursor.FromPcm(
            10_000,
            TestPcm.Concat(firstSpeech, pause, secondSpeech, ending));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Diagnostics.SpeechEndSampleOffset.Should().Be(10_000 + Samples(1_600));
        result.Audio!.Pcm16.Length.Should().Be(Samples(2_800) * sizeof(short));
    }

    [Fact]
    public async Task CaptureAsync_OnePointTwoSecondsOfSilence_EndpointsWithoutReadingFurther()
    {
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(
                TestPcm.Speech(200, 0.20),
                TestPcm.Silence(1_200, 0),
                TestPcm.Speech(200, 0.20)));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(1_400));
        result.Diagnostics.SpeechEndSampleOffset.Should().Be(Samples(200));
        cursor.ReadFrameCount.Should().Be(70);
    }

    [Fact]
    public async Task CaptureAsync_ContinuousSpeech_HardStopsAtTenSeconds()
    {
        await using var cursor = TestCursor.FromPcm(25_000, TestPcm.Speech(12_000, 0.20));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Audio!.Duration.Should().Be(TimeSpan.FromSeconds(10));
        result.Audio.Pcm16.Length.Should().Be(320_000);
        result.Diagnostics.SpeechEndSampleOffset.Should().Be(25_000 + Samples(10_000));
        cursor.ReadFrameCount.Should().Be(500);
    }

    [Fact]
    public async Task CaptureAsync_MaximumShorterThanRoundedCandidate_CapsCandidateExactly()
    {
        const long startSampleOffset = 90_000;
        await using var cursor = TestCursor.FromPcm(
            startSampleOffset,
            TestPcm.Speech(160, 0.20));
        var options = new VoiceActivityOptions(
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(159),
            0.80)
        {
            PreRollDuration = TimeSpan.FromMilliseconds(20),
            InitialSilenceTimeout = TimeSpan.FromMilliseconds(159),
        };
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            options,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Audio!.Pcm16.Length.Should().Be(5_088);
        result.Audio.Duration.Should().Be(TimeSpan.FromMilliseconds(159));
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(159));
        result.Diagnostics.ObservedDuration.Should().Be(TimeSpan.FromMilliseconds(160));
        result.Diagnostics.CapturedDuration.Should().Be(TimeSpan.FromMilliseconds(159));
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(startSampleOffset);
        result.Diagnostics.SpeechEndSampleOffset.Should().Be(startSampleOffset + 2_544);
        progress.Should().ContainSingle();
    }

    [Fact]
    public async Task CaptureAsync_FortyMillisecondSpike_DoesNotConfirmSpeech()
    {
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(TestPcm.Speech(40, 0.90), TestPcm.Silence(3_960, 0.01)));
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeFalse();
        progress.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_QuietContinuation_DoesNotPrematurelyEndpoint()
    {
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(
                TestPcm.Speech(160, 0.20),
                TestPcm.Speech(1_300, 0.02),
                TestPcm.Silence(1_200, 0)));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Diagnostics.SpeechEndSampleOffset.Should().Be(Samples(1_460));
        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(2_660));
    }

    [Theory]
    [InlineData(0.65, false)]
    [InlineData(0.90, true)]
    public async Task CaptureAsync_HigherSensitivityDetectsQuieterSpeech(
        double sensitivity,
        bool expected)
    {
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(TestPcm.Speech(200, 0.028), TestPcm.Silence(1_200, 0)));
        var options = VoiceActivityOptions.ContinuousDefault with { Sensitivity = sensitivity };

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            options,
            _ => { },
            CancellationToken.None);

        result.SpeechDetected.Should().Be(expected);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(138)]
    [InlineData(642)]
    [InlineData(2_046)]
    public async Task CaptureAsync_ArbitraryEvenSourceChunks_AreReframed(int chunkSize)
    {
        await using var cursor = TestCursor.FromPcm(
            41_000,
            TestPcm.Concat(TestPcm.Speech(200, 0.20), TestPcm.Silence(1_200, 0)),
            chunkSize);

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(1_400));
        result.Diagnostics.ObservedDuration.Should().Be(TimeSpan.FromMilliseconds(1_400));
    }

    [Fact]
    public async Task CaptureAsync_UnsupportedCursorFormat_RejectsBeforeReading()
    {
        await using var cursor = TestCursor.Empty(
            0,
            new AudioFormat(48_000, 2, 32, true));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        cursor.ReadFrameCount.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(638)]
    public async Task CaptureAsync_IncompletePcm_ThrowsUnsupportedFormat(int pcmBytes)
    {
        await using var cursor = TestCursor.FromRawFrame(
            0,
            new byte[pcmBytes],
            endSampleOffset: pcmBytes / sizeof(short),
            duration: TimeSpan.FromTicks((pcmBytes / sizeof(short)) * 625L));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task CaptureAsync_CoherentGap_ThrowsUnsupportedFormat()
    {
        await AssertInvalidSequenceAsync(secondStart: 640, secondEnd: 960);
    }

    [Fact]
    public async Task CaptureAsync_CoherentOverlap_ThrowsUnsupportedFormat()
    {
        await AssertInvalidSequenceAsync(secondStart: 160, secondEnd: 480);
    }

    private static async Task AssertInvalidSequenceAsync(long secondStart, long secondEnd)
    {
        await using var cursor = TestCursor.WithFrames(
            0,
            TestCursor.Frame(0, 320, TestPcm.Silence(20, 0)),
            TestCursor.Frame(secondStart, secondEnd, TestPcm.Silence(20, 0)));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task CaptureAsync_CandidateInFlightAtInitialSilenceBoundary_ReturnsNoSpeech()
    {
        await using var cursor = TestCursor.FromPcm(
            30_000,
            TestPcm.Concat(
                TestPcm.Silence(3_860, 0.01),
                TestPcm.Speech(140, 0.20),
                TestPcm.Speech(160, 0.20)));
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeFalse();
        result.Duration.Should().Be(TimeSpan.FromSeconds(4));
        result.Diagnostics.ObservedDuration.Should().Be(TimeSpan.FromSeconds(4));
        result.Diagnostics.SpeechStartSampleOffset.Should().BeNull();
        progress.Should().BeEmpty();
        cursor.ReadFrameCount.Should().Be(200);
    }

    [Fact]
    public async Task CaptureAsync_SuccessProgressPayloadIsExactAndSynchronousOnce()
    {
        const long startSampleOffset = 123_456;
        await using var cursor = TestCursor.FromPcm(
            startSampleOffset,
            TestPcm.Concat(TestPcm.Speech(160, 0.20), TestPcm.Silence(1_200, 0)));
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            value =>
            {
                cursor.ReadFrameCount.Should().Be(8);
                progress.Add(value);
            },
            CancellationToken.None);

        var expectedPeak = Math.Round(short.MaxValue * 0.20) / 32_768d;
        progress.Should().ContainSingle().Which.Should().Be(
            new VoiceActivityProgress(startSampleOffset, 0.01, expectedPeak));
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(startSampleOffset);
    }

    [Theory]
    [InlineData(0.65, 0.020, 0.070, 0.044)]
    [InlineData(0.775, 0.020, 0.057, 0.036)]
    [InlineData(0.90, 0.020, 0.044, 0.028)]
    [InlineData(0.65, 0.001, 0.012, 0.008)]
    public void MapThresholds_UsesExactMultipliersAndAbsoluteFloors(
        double sensitivity,
        double noiseFloor,
        double expectedStart,
        double expectedContinue)
    {
        var thresholds = AdaptiveVoiceActivityDetector.MapThresholds(sensitivity, noiseFloor);

        thresholds.Start.Should().BeApproximately(expectedStart, 1e-12);
        thresholds.Continue.Should().BeApproximately(expectedContinue, 1e-12);
    }

    [Fact]
    public void UpdateNoiseFloor_UsesExactNinetyFiveFiveWeighting()
    {
        AdaptiveVoiceActivityDetector.UpdateNoiseFloor(0.04, 0.02)
            .Should().BeApproximately(0.039, 1e-12);
    }

    [Fact]
    public async Task CaptureAsync_ZeroLengthSourceFrame_ThrowsUnsupportedFormat()
    {
        await using var cursor = TestCursor.WithFrames(
            900,
            new SequencedAudioFrame(ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, 900, 900));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task CaptureAsync_IncoherentSourceDuration_ThrowsUnsupportedFormat()
    {
        await using var cursor = TestCursor.FromRawFrame(
            0,
            TestPcm.Silence(20, 0),
            320,
            TimeSpan.FromMilliseconds(19));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task CaptureAsync_AbsoluteOffsetOverflow_ThrowsUnsupportedFormat()
    {
        var start = long.MaxValue - 100;
        await using var cursor = TestCursor.FromRawFrame(
            start,
            TestPcm.Silence(20, 0),
            long.MaxValue,
            TimeSpan.FromMilliseconds(20));

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task CaptureAsync_ThresholdEquality_CountsAsSpeech()
    {
        const short sample = 655;
        var threshold = sample / 32_768d;
        var ambient = new AmbientNoiseSnapshot(threshold / 3.5, TimeSpan.FromSeconds(3), 150);
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(TestPcm.Constant(160, sample), TestPcm.Silence(1_200, 0)));
        var options = VoiceActivityOptions.ContinuousDefault with { Sensitivity = 0.65 };

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            ambient,
            options,
            _ => { },
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
    }

    [Fact]
    public async Task CaptureAsync_RejectedCandidateMovesOnceIntoExactPreRoll()
    {
        var oldSilence = TestPcm.Silence(200, 0.005);
        var rejectedCandidate = TestPcm.Speech(100, 0.20);
        var reset = TestPcm.Silence(20, 0.01);
        var speech = TestPcm.Speech(160, 0.30);
        var trailing = TestPcm.Silence(1_200, 0);
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(oldSilence, rejectedCandidate, reset, speech, trailing));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        var expectedPreRoll = TestPcm.Concat(
            oldSilence[^(Samples(180) * sizeof(short))..],
            rejectedCandidate,
            reset);
        result.Audio!.Pcm16.Span[..expectedPreRoll.Length]
            .SequenceEqual(expectedPreRoll)
            .Should().BeTrue();
        result.Audio.Pcm16.Length.Should().Be(Samples(300 + 160 + 1_200) * sizeof(short));
        result.Diagnostics.SpeechStartSampleOffset.Should().Be(Samples(320));
    }

    [Fact]
    public async Task CaptureAsync_ConfirmedSpeechWhenSourceEnds_ReturnsAvailableAudio()
    {
        await using var cursor = TestCursor.FromPcm(
            22_000,
            TestPcm.Concat(TestPcm.Speech(200, 0.20), TestPcm.Silence(400, 0)));

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(600));
        result.Diagnostics.SpeechEndSampleOffset.Should().Be(22_000 + Samples(200));
    }

    [Fact]
    public async Task CaptureAsync_NormalSourceEndWithoutConfirmation_ReturnsNoSpeech()
    {
        await using var cursor = TestCursor.FromPcm(5_000, TestPcm.Silence(600, 0.01));
        var progress = new List<VoiceActivityProgress>();

        var result = await new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        result.SpeechDetected.Should().BeFalse();
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(600));
        result.Diagnostics.ObservedDuration.Should().Be(TimeSpan.FromMilliseconds(600));
        result.Diagnostics.SpeechStartSampleOffset.Should().BeNull();
        progress.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_CancellationDuringCursorWait_ClearsPoolAndKeepsCursorOwnedByCaller()
    {
        var pool = new RecordingArrayPool(641, 340_000);
        await using var cursor = TestCursor.UntilCancelled(70_000);
        using var cancellation = new CancellationTokenSource();
        var detector = new AdaptiveVoiceActivityDetector(pool);
        var progress = new List<VoiceActivityProgress>();
        var capture = detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            cancellation.Token);
        await cursor.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        var action = async () => await capture;
        await action.Should().ThrowAsync<OperationCanceledException>();
        pool.AssertAllReturnedAndCleared();
        cursor.DisposeCount.Should().Be(0);
        progress.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_CancellationFromProgressInsideLargeChunk_StopsBeforeNextFrame()
    {
        var pool = new RecordingArrayPool(777, 340_000);
        await using var cursor = TestCursor.FromPcm(
            18_000,
            TestPcm.Concat(TestPcm.Speech(400, 0.20), TestPcm.Silence(1_200, 0)),
            chunkSize: Samples(1_600) * sizeof(short));
        using var cancellation = new CancellationTokenSource();
        var detector = new AdaptiveVoiceActivityDetector(pool);
        var progressCount = 0;

        var action = () => detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ =>
            {
                progressCount++;
                cancellation.Cancel();
            },
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        progressCount.Should().Be(1);
        cursor.ReadFrameCount.Should().Be(1);
        pool.AssertAllReturnedAndCleared();
        cursor.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_SuccessReturnsOwnedExactCopyAndClearsOversizedPoolArrays()
    {
        var pool = new RecordingArrayPool(713, 330_000);
        await using var cursor = TestCursor.FromPcm(
            0,
            TestPcm.Concat(TestPcm.Speech(200, 0.20), TestPcm.Silence(1_200, 0)));
        var detector = new AdaptiveVoiceActivityDetector(pool);

        var result = await detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        result.Audio!.Pcm16.Length.Should().Be(44_800);
        pool.AssertAllReturnedAndCleared();
        MemoryMarshal.TryGetArray(result.Audio.Pcm16, out var owned).Should().BeTrue();
        pool.ReturnedArrays.Should().NotContain(array => ReferenceEquals(array, owned.Array));
        result.Audio.Pcm16.ToArray().Should().Contain(value => value != 0);
    }

    [Fact]
    public async Task CaptureAsync_CursorFailureClearsOversizedPoolArrays()
    {
        var pool = new RecordingArrayPool(701, 340_000);
        await using var cursor = TestCursor.Failing(0, new InvalidOperationException("cursor failure"));
        var detector = new AdaptiveVoiceActivityDetector(pool);
        var progress = new List<VoiceActivityProgress>();

        var action = () => detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            progress.Add,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        pool.AssertAllReturnedAndCleared();
        cursor.DisposeCount.Should().Be(0);
        progress.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_SourceValidationFailureClearsOversizedPoolArrays()
    {
        var pool = new RecordingArrayPool(731, 340_000);
        await using var cursor = TestCursor.FromRawFrame(
            0,
            TestPcm.Silence(20, 0),
            320,
            TimeSpan.FromMilliseconds(19));
        var detector = new AdaptiveVoiceActivityDetector(pool);

        var action = () => detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        pool.AssertAllReturnedAndCleared();
        cursor.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_ProgressFailurePropagatesAfterExactlyOneCallAndClearsPool()
    {
        var pool = new RecordingArrayPool(699, 340_000);
        await using var cursor = TestCursor.FromPcm(0, TestPcm.Speech(400, 0.20));
        var detector = new AdaptiveVoiceActivityDetector(pool);
        var callCount = 0;

        var action = () => detector.CaptureAsync(
            cursor,
            Ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ =>
            {
                callCount++;
                throw new InvalidOperationException("progress failure");
            },
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        callCount.Should().Be(1);
        cursor.ReadFrameCount.Should().Be(8);
        pool.AssertAllReturnedAndCleared();
        cursor.DisposeCount.Should().Be(0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.649)]
    [InlineData(0.901)]
    public async Task CaptureAsync_InvalidSensitivityIsRejected(double sensitivity)
    {
        await using var cursor = TestCursor.Empty(0);
        var options = VoiceActivityOptions.ContinuousDefault with { Sensitivity = sensitivity };

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            Ambient,
            options,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        cursor.ReadFrameCount.Should().Be(0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    public async Task CaptureAsync_InvalidAmbientNoiseIsRejected(double noiseFloor)
    {
        await using var cursor = TestCursor.Empty(0);
        var ambient = Ambient with { NoiseFloorRms = noiseFloor };

        var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
            cursor,
            ambient,
            VoiceActivityOptions.ContinuousDefault,
            _ => { },
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        cursor.ReadFrameCount.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_InvalidDurationsAreRejected()
    {
        var invalidOptions = new[]
        {
            VoiceActivityOptions.ContinuousDefault with { MinimumSpeechDuration = TimeSpan.Zero },
            VoiceActivityOptions.ContinuousDefault with { SilenceTimeout = TimeSpan.Zero },
            VoiceActivityOptions.ContinuousDefault with { MaximumCommandDuration = TimeSpan.Zero },
            VoiceActivityOptions.ContinuousDefault with { PreRollDuration = TimeSpan.Zero },
            VoiceActivityOptions.ContinuousDefault with { InitialSilenceTimeout = TimeSpan.Zero },
            VoiceActivityOptions.ContinuousDefault with
            {
                PreRollDuration = TimeSpan.FromSeconds(11),
            },
            VoiceActivityOptions.ContinuousDefault with
            {
                SilenceTimeout = TimeSpan.FromSeconds(11),
            },
            VoiceActivityOptions.ContinuousDefault with
            {
                MinimumSpeechDuration = TimeSpan.FromSeconds(11),
            },
            VoiceActivityOptions.ContinuousDefault with
            {
                MaximumCommandDuration = TimeSpan.FromSeconds(11),
            },
        };

        foreach (var options in invalidOptions)
        {
            await using var cursor = TestCursor.Empty(0);
            var action = () => new AdaptiveVoiceActivityDetector().CaptureAsync(
                cursor,
                Ambient,
                options,
                _ => { },
                CancellationToken.None);

            await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
            cursor.ReadFrameCount.Should().Be(0);
        }
    }

    private static AmbientNoiseSnapshot Ambient { get; } =
        new(0.01, TimeSpan.FromSeconds(3), 150);

    private static int Samples(int milliseconds) => checked(16 * milliseconds);

    private static class TestPcm
    {
        public static byte[] Silence(int durationMs, double amplitude) =>
            Speech(durationMs, amplitude);

        public static byte[] Speech(int durationMs, double amplitude)
        {
            var sample = (short)Math.Round(short.MaxValue * amplitude);
            return Constant(durationMs, sample);
        }

        public static byte[] Constant(int durationMs, short sample)
        {
            var pcm = new byte[Samples(durationMs) * sizeof(short)];
            for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, sizeof(short)), sample);
            }

            return pcm;
        }

        public static byte[] Concat(params byte[][] segments) =>
            segments.SelectMany(segment => segment).ToArray();
    }

    private sealed class TestCursor : IVoiceAudioCursor
    {
        private readonly IReadOnlyList<SequencedAudioFrame> _frames;
        private readonly bool _waitForCancellation;
        private readonly Exception? _failure;

        private TestCursor(
            long startSampleOffset,
            AudioFormat format,
            IReadOnlyList<SequencedAudioFrame> frames,
            bool waitForCancellation,
            Exception? failure = null)
        {
            StartSampleOffset = startSampleOffset;
            Format = format;
            _frames = frames;
            _waitForCancellation = waitForCancellation;
            _failure = failure;
        }

        public AudioFormat Format { get; }

        public long StartSampleOffset { get; }

        public int ReadFrameCount { get; private set; }

        public int DisposeCount { get; private set; }

        public TaskCompletionSource Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TestCursor FromPcm(long startSampleOffset, byte[] pcm16, int chunkSize = 640)
        {
            chunkSize.Should().BePositive();
            (chunkSize % sizeof(short)).Should().Be(0);
            var frames = new List<SequencedAudioFrame>();
            var sampleOffset = startSampleOffset;
            for (var offset = 0; offset < pcm16.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, pcm16.Length - offset);
                var copy = pcm16.AsMemory(offset, length).ToArray();
                var samples = length / sizeof(short);
                frames.Add(Frame(sampleOffset, checked(sampleOffset + samples), copy));
                sampleOffset = checked(sampleOffset + samples);
            }

            return new TestCursor(
                startSampleOffset,
                AudioFormat.Pcm16KhzMono,
                frames,
                waitForCancellation: false);
        }

        public static TestCursor FromRawFrame(
            long startSampleOffset,
            byte[] pcm16,
            long endSampleOffset,
            TimeSpan duration) =>
            new(
                startSampleOffset,
                AudioFormat.Pcm16KhzMono,
                [new SequencedAudioFrame(pcm16, duration, startSampleOffset, endSampleOffset)],
                waitForCancellation: false);

        public static TestCursor WithFrames(
            long startSampleOffset,
            params SequencedAudioFrame[] frames) =>
            new(
                startSampleOffset,
                AudioFormat.Pcm16KhzMono,
                frames,
                waitForCancellation: false);

        public static TestCursor Empty(long startSampleOffset, AudioFormat? format = null) =>
            new(
                startSampleOffset,
                format ?? AudioFormat.Pcm16KhzMono,
                [],
                waitForCancellation: false);

        public static TestCursor UntilCancelled(long startSampleOffset) =>
            new(
                startSampleOffset,
                AudioFormat.Pcm16KhzMono,
                [],
                waitForCancellation: true);

        public static TestCursor Failing(long startSampleOffset, Exception failure) =>
            new(
                startSampleOffset,
                AudioFormat.Pcm16KhzMono,
                [],
                waitForCancellation: false,
                failure);

        public static SequencedAudioFrame Frame(long start, long end, byte[] pcm16) =>
            new(
                pcm16,
                TimeSpan.FromTicks((end - start) * 625),
                start,
                end);

        public async IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var frame in _frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadFrameCount++;
                await Task.Yield();
                yield return frame;
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            if (_waitForCancellation)
            {
                Waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingArrayPool : ArrayPool<byte>
    {
        private readonly Queue<byte[]> _available;

        public RecordingArrayPool(int analysisLength, int commandLength)
        {
            _available = new Queue<byte[]>(
            [
                Enumerable.Repeat((byte)0xA5, analysisLength).ToArray(),
                Enumerable.Repeat((byte)0x5A, commandLength).ToArray(),
            ]);
        }

        public List<byte[]> ReturnedArrays { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            _available.Should().NotBeEmpty();
            var array = _available.Dequeue();
            array.Length.Should().BeGreaterThanOrEqualTo(minimumLength);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            clearArray.Should().BeFalse();
            ReturnedArrays.Add(array);
        }

        public void AssertAllReturnedAndCleared()
        {
            _available.Should().BeEmpty();
            ReturnedArrays.Should().HaveCount(2);
            ReturnedArrays.Should().OnlyContain(array => array.All(value => value == 0));
        }
    }
}
