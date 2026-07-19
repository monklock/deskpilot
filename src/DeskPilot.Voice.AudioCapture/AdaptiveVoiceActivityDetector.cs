using System.Buffers;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Captures bounded commands using adaptive energy endpointing.</summary>
public sealed class AdaptiveVoiceActivityDetector
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = sizeof(short);
    private const int AnalysisFrameSamples = 320;
    private const int AnalysisFrameBytes = 640;
    private const double MinimumSensitivity = 0.65;
    private const double MaximumSensitivity = 0.90;
    private const double AbsoluteStartFloor = 0.012;
    private const double AbsoluteContinueFloor = 0.008;
    private static readonly TimeSpan MaximumSupportedCommandDuration = TimeSpan.FromSeconds(10);
    private readonly ArrayPool<byte> _pool;

    /// <summary>Creates an adaptive voice activity detector.</summary>
    public AdaptiveVoiceActivityDetector()
        : this(ArrayPool<byte>.Shared)
    {
    }

    internal AdaptiveVoiceActivityDetector(ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
    }

    /// <summary>Captures one bounded command from a sequenced audio cursor.</summary>
    public async Task<VoiceActivityResult> CaptureAsync(
        IVoiceAudioCursor cursor,
        AmbientNoiseSnapshot ambientNoise,
        VoiceActivityOptions options,
        Action<VoiceActivityProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(ambientNoise);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        Validate(cursor, ambientNoise, options);
        cancellationToken.ThrowIfCancellationRequested();

        var minimumSpeechSamples = ToSamples(options.MinimumSpeechDuration, nameof(options));
        var preRollSamples = ToSamples(options.PreRollDuration, nameof(options));
        var initialSilenceSamples = ToSamples(options.InitialSilenceTimeout, nameof(options));
        var endSilenceSamples = ToSamples(options.EndSilenceTimeout, nameof(options));
        var maximumCommandSamples = ToSamples(options.MaximumCommandDuration, nameof(options));
        var candidateCapacityBytes = checked(
            DivideRoundUp(minimumSpeechSamples, AnalysisFrameSamples)
            * AnalysisFrameBytes);
        var preRollCapacityBytes = checked(
            DivideRoundUp(preRollSamples, AnalysisFrameSamples)
            * AnalysisFrameBytes);
        var maximumCommandBytes = checked(maximumCommandSamples * BytesPerSample);
        int commandRentLength;
        try
        {
            commandRentLength = checked(maximumCommandBytes + candidateCapacityBytes);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Voice activity durations exceed the supported in-memory command bound.");
        }

        var analysisBuffer = _pool.Rent(AnalysisFrameBytes);
        byte[]? commandBuffer = null;
        try
        {
            commandBuffer = _pool.Rent(commandRentLength);
            var state = new CaptureState(
                cursor.StartSampleOffset,
                ambientNoise.NoiseFloorRms,
                options.Sensitivity,
                minimumSpeechSamples,
                preRollCapacityBytes,
                initialSilenceSamples,
                endSilenceSamples,
                maximumCommandBytes);
            var analysisLength = 0;
            var expectedSourceOffset = cursor.StartSampleOffset;
            var completed = false;

            await foreach (var sourceFrame in cursor.ReadFramesAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                ValidateSourceFrame(sourceFrame, expectedSourceOffset);
                expectedSourceOffset = sourceFrame.EndSampleOffset;

                var source = sourceFrame.Pcm16;
                var sourceOffset = 0;
                while (sourceOffset < source.Length && !completed)
                {
                    var copyLength = Math.Min(
                        AnalysisFrameBytes - analysisLength,
                        source.Length - sourceOffset);
                    source.Span.Slice(sourceOffset, copyLength).CopyTo(
                        analysisBuffer.AsSpan(analysisLength, copyLength));
                    sourceOffset += copyLength;
                    analysisLength += copyLength;

                    if (analysisLength == AnalysisFrameBytes)
                    {
                        completed = ProcessAnalysisFrame(
                            analysisBuffer.AsSpan(0, AnalysisFrameBytes),
                            commandBuffer,
                            state,
                            progress);
                        analysisLength = 0;
                    }
                }

                if (completed)
                {
                    break;
                }
            }

            if (!completed && analysisLength != 0)
            {
                throw UnsupportedFormat(
                    "PCM16 microphone stream ended before a complete 20 ms analysis frame.");
            }

            return CreateResult(commandBuffer, state);
        }
        finally
        {
            if (commandBuffer is not null)
            {
                Array.Clear(commandBuffer);
                _pool.Return(commandBuffer, clearArray: false);
            }

            Array.Clear(analysisBuffer);
            _pool.Return(analysisBuffer, clearArray: false);
        }
    }

    private static bool ProcessAnalysisFrame(
        ReadOnlySpan<byte> frame,
        byte[] commandBuffer,
        CaptureState state,
        Action<VoiceActivityProgress> progress)
    {
        var frameStartOffset = AddOffset(state.CursorStartSampleOffset, state.TotalObservedSamples);
        var frameEndOffset = AddOffset(frameStartOffset, AnalysisFrameSamples);
        state.TotalObservedSamples = checked(state.TotalObservedSamples + AnalysisFrameSamples);
        var rms = PcmRms.Calculate(frame);
        state.PeakRms = Math.Max(state.PeakRms, rms);

        if (!state.SpeechConfirmed)
        {
            var thresholds = MapThresholds(state.Sensitivity, state.AdaptiveNoiseFloor);
            if (rms >= thresholds.Start)
            {
                if (state.CandidateSpeechSamples == 0)
                {
                    state.CandidateStartSampleOffset = frameStartOffset;
                }

                frame.CopyTo(commandBuffer.AsSpan(
                    state.MaximumCommandBytes + state.CandidateLength,
                    AnalysisFrameBytes));
                state.CandidateLength += AnalysisFrameBytes;
                state.CandidateSpeechSamples += AnalysisFrameSamples;

                if (state.CandidateSpeechSamples >= state.MinimumSpeechSamples)
                {
                    ConfirmSpeech(commandBuffer, state, frameEndOffset);
                    progress(new VoiceActivityProgress(
                        state.SpeechStartSampleOffset!.Value,
                        state.AdaptiveNoiseFloor,
                        state.PeakRms));
                    return state.CommandLength >= state.MaximumCommandBytes;
                }
            }
            else
            {
                state.AdaptiveNoiseFloor =
                    (0.95 * state.AdaptiveNoiseFloor) + (0.05 * rms);
                if (state.CandidateLength > 0)
                {
                    AppendPreRoll(
                        commandBuffer.AsSpan(
                            state.MaximumCommandBytes,
                            state.CandidateLength),
                        commandBuffer,
                        state);
                    state.CandidateLength = 0;
                    state.CandidateSpeechSamples = 0;
                    state.CandidateStartSampleOffset = null;
                }

                AppendPreRoll(frame, commandBuffer, state);
            }

            return state.TotalObservedSamples >= state.InitialSilenceSamples;
        }

        var availableBytes = state.MaximumCommandBytes - state.CommandLength;
        var copiedBytes = Math.Min(AnalysisFrameBytes, availableBytes);
        if (copiedBytes > 0)
        {
            frame[..copiedBytes].CopyTo(commandBuffer.AsSpan(state.CommandLength, copiedBytes));
            state.CommandLength += copiedBytes;
        }

        var copiedSamples = copiedBytes / BytesPerSample;
        var continuationThreshold = MapThresholds(
            state.Sensitivity,
            state.AdaptiveNoiseFloor).Continue;
        if (rms >= continuationThreshold)
        {
            state.TrailingSilenceSamples = 0;
            state.SpeechEndSampleOffset = AddOffset(frameStartOffset, copiedSamples);
        }
        else
        {
            state.TrailingSilenceSamples = checked(
                state.TrailingSilenceSamples + copiedSamples);
        }

        return state.CommandLength >= state.MaximumCommandBytes
            || state.TrailingSilenceSamples >= state.EndSilenceSamples;
    }

    private static void ConfirmSpeech(
        byte[] commandBuffer,
        CaptureState state,
        long frameEndOffset)
    {
        var retainedPreRollLength = Math.Min(
            state.PreRollLength,
            state.MaximumCommandBytes - state.CandidateLength);
        if (retainedPreRollLength < state.PreRollLength)
        {
            commandBuffer.AsSpan(
                state.PreRollLength - retainedPreRollLength,
                retainedPreRollLength).CopyTo(commandBuffer);
        }

        commandBuffer.AsSpan(
            state.MaximumCommandBytes,
            state.CandidateLength).CopyTo(
                commandBuffer.AsSpan(retainedPreRollLength, state.CandidateLength));
        state.CommandLength = retainedPreRollLength + state.CandidateLength;
        state.SpeechConfirmed = true;
        state.SpeechStartSampleOffset = state.CandidateStartSampleOffset;
        state.SpeechEndSampleOffset = frameEndOffset;
        state.PreRollLength = 0;
        state.CandidateLength = 0;
        state.CandidateSpeechSamples = 0;
        state.CandidateStartSampleOffset = null;
    }

    private static void AppendPreRoll(
        ReadOnlySpan<byte> source,
        byte[] commandBuffer,
        CaptureState state)
    {
        if (source.Length >= state.PreRollCapacityBytes)
        {
            source[^state.PreRollCapacityBytes..].CopyTo(
                commandBuffer.AsSpan(0, state.PreRollCapacityBytes));
            state.PreRollLength = state.PreRollCapacityBytes;
            return;
        }

        var overflow = Math.Max(
            0,
            state.PreRollLength + source.Length - state.PreRollCapacityBytes);
        if (overflow > 0)
        {
            commandBuffer.AsSpan(overflow, state.PreRollLength - overflow).CopyTo(commandBuffer);
            state.PreRollLength -= overflow;
        }

        source.CopyTo(commandBuffer.AsSpan(state.PreRollLength, source.Length));
        state.PreRollLength += source.Length;
    }

    private static VoiceActivityResult CreateResult(byte[] commandBuffer, CaptureState state)
    {
        var observedDuration = FromSamples(state.TotalObservedSamples);
        if (!state.SpeechConfirmed)
        {
            return new VoiceActivityResult(false, observedDuration, null)
            {
                Diagnostics = new VoiceActivityDiagnostics(
                    observedDuration,
                    TimeSpan.Zero,
                    null,
                    null,
                    state.AdaptiveNoiseFloor,
                    state.PeakRms),
            };
        }

        var captured = commandBuffer.AsSpan(0, state.CommandLength).ToArray();
        var capturedDuration = TimeSpan.FromSeconds(
            state.CommandLength / (double)(SampleRate * BytesPerSample));
        var audio = new CapturedCommandAudio(
            captured,
            AudioFormat.Pcm16KhzMono,
            capturedDuration);
        return new VoiceActivityResult(true, capturedDuration, audio)
        {
            Diagnostics = new VoiceActivityDiagnostics(
                observedDuration,
                capturedDuration,
                state.SpeechStartSampleOffset,
                state.SpeechEndSampleOffset,
                state.AdaptiveNoiseFloor,
                state.PeakRms),
        };
    }

    private static (double Start, double Continue) MapThresholds(
        double sensitivity,
        double noiseFloor)
    {
        var normalized = (sensitivity - MinimumSensitivity)
            / (MaximumSensitivity - MinimumSensitivity);
        var startMultiplier = 3.5 - (1.3 * normalized);
        var continueMultiplier = 2.2 - (0.8 * normalized);
        return (
            Math.Max(AbsoluteStartFloor, noiseFloor * startMultiplier),
            Math.Max(AbsoluteContinueFloor, noiseFloor * continueMultiplier));
    }

    private static void Validate(
        IVoiceAudioCursor cursor,
        AmbientNoiseSnapshot ambientNoise,
        VoiceActivityOptions options)
    {
        if (cursor.Format != AudioFormat.Pcm16KhzMono || cursor.StartSampleOffset < 0)
        {
            throw UnsupportedFormat("Adaptive VAD requires mono 16 kHz PCM16 audio.");
        }

        if (!double.IsFinite(ambientNoise.NoiseFloorRms)
            || ambientNoise.NoiseFloorRms is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ambientNoise),
                ambientNoise.NoiseFloorRms,
                "Ambient RMS must be finite and normalized to the inclusive range 0..1.");
        }

        if (ambientNoise.WindowDuration < TimeSpan.Zero || ambientNoise.FrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ambientNoise),
                "Ambient diagnostics cannot contain negative values.");
        }

        if (!double.IsFinite(options.Sensitivity)
            || options.Sensitivity is < MinimumSensitivity or > MaximumSensitivity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Sensitivity,
                "Voice sensitivity must be between 0.65 and 0.90.");
        }

        if (options.MinimumSpeechDuration <= TimeSpan.Zero
            || options.PreRollDuration <= TimeSpan.Zero
            || options.InitialSilenceTimeout <= TimeSpan.Zero
            || options.EndSilenceTimeout <= TimeSpan.Zero
            || options.MaximumCommandDuration <= TimeSpan.Zero
            || options.MaximumCommandDuration > MaximumSupportedCommandDuration
            || options.MinimumSpeechDuration > options.MaximumCommandDuration
            || options.PreRollDuration > options.MaximumCommandDuration
            || options.InitialSilenceTimeout > options.MaximumCommandDuration
            || options.EndSilenceTimeout > options.MaximumCommandDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Voice activity durations must be positive and bounded by the command limit.");
        }
    }

    private static void ValidateSourceFrame(
        SequencedAudioFrame frame,
        long expectedStartSampleOffset)
    {
        if (frame.Pcm16.IsEmpty || frame.Pcm16.Length % BytesPerSample != 0)
        {
            throw UnsupportedFormat("PCM16 source frames must contain complete samples.");
        }

        var sampleCount = frame.Pcm16.Length / BytesPerSample;
        long expectedEndSampleOffset;
        try
        {
            expectedEndSampleOffset = checked(frame.StartSampleOffset + sampleCount);
        }
        catch (OverflowException exception)
        {
            throw UnsupportedFormat("PCM16 source frame offsets exceed the supported range.", exception);
        }

        if (frame.StartSampleOffset != expectedStartSampleOffset
            || frame.EndSampleOffset != expectedEndSampleOffset
            || frame.Duration != FromSamples(sampleCount))
        {
            throw UnsupportedFormat(
                "PCM16 source frame bytes, duration, and absolute offsets must be contiguous.");
        }
    }

    private static int ToSamples(TimeSpan duration, string parameterName)
    {
        var samples = Math.Ceiling(duration.TotalSeconds * SampleRate);
        if (!double.IsFinite(samples) || samples <= 0 || samples > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                duration,
                "Voice activity duration exceeds the supported sample range.");
        }

        return (int)samples;
    }

    private static int DivideRoundUp(int value, int divisor) =>
        ((value - 1) / divisor) + 1;

    private static long AddOffset(long offset, int samples)
    {
        try
        {
            return checked(offset + samples);
        }
        catch (OverflowException exception)
        {
            throw UnsupportedFormat("PCM16 source frame offsets exceed the supported range.", exception);
        }
    }

    private static TimeSpan FromSamples(int samples) =>
        TimeSpan.FromTicks(checked((long)samples * (TimeSpan.TicksPerSecond / SampleRate)));

    private static AudioCaptureException UnsupportedFormat(
        string message,
        Exception? innerException = null) =>
        new(AudioInputResultCode.UnsupportedFormat, message, innerException);

    private sealed class CaptureState
    {
        public CaptureState(
            long cursorStartSampleOffset,
            double ambientNoiseFloor,
            double sensitivity,
            int minimumSpeechSamples,
            int preRollCapacityBytes,
            int initialSilenceSamples,
            int endSilenceSamples,
            int maximumCommandBytes)
        {
            CursorStartSampleOffset = cursorStartSampleOffset;
            AdaptiveNoiseFloor = Math.Max(ambientNoiseFloor, 0.001);
            Sensitivity = sensitivity;
            MinimumSpeechSamples = minimumSpeechSamples;
            PreRollCapacityBytes = preRollCapacityBytes;
            InitialSilenceSamples = initialSilenceSamples;
            EndSilenceSamples = endSilenceSamples;
            MaximumCommandBytes = maximumCommandBytes;
        }

        public long CursorStartSampleOffset { get; }

        public double Sensitivity { get; }

        public int MinimumSpeechSamples { get; }

        public int PreRollCapacityBytes { get; }

        public int InitialSilenceSamples { get; }

        public int EndSilenceSamples { get; }

        public int MaximumCommandBytes { get; }

        public double AdaptiveNoiseFloor { get; set; }

        public double PeakRms { get; set; }

        public int TotalObservedSamples { get; set; }

        public int CandidateSpeechSamples { get; set; }

        public int CandidateLength { get; set; }

        public long? CandidateStartSampleOffset { get; set; }

        public int PreRollLength { get; set; }

        public bool SpeechConfirmed { get; set; }

        public int CommandLength { get; set; }

        public int TrailingSilenceSamples { get; set; }

        public long? SpeechStartSampleOffset { get; set; }

        public long? SpeechEndSampleOffset { get; set; }
    }
}
