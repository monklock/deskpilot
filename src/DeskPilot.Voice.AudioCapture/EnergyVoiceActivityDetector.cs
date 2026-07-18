using System.Buffers;
using System.Buffers.Binary;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Captures bounded normalized command audio using an energy threshold.</summary>
public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = sizeof(short);
    private const int AnalysisFrameSamples = SampleRate / 50;
    private const int AnalysisFrameBytes = AnalysisFrameSamples * BytesPerSample;
    private const double MinimumSensitivity = 0.65;
    private const double MaximumSensitivity = 0.90;
    private const double MinimumRmsThreshold = 0.01;
    private const double MaximumRmsThreshold = 0.05;

    /// <inheritdoc />
    public async Task<VoiceActivityResult> CaptureAsync(
        IAudioCaptureSession input,
        VoiceActivityOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(input, options);

        var maximumSamples = checked((int)Math.Ceiling(
            options.MaximumCommandDuration.TotalSeconds * SampleRate));
        var minimumSpeechSamples = checked((int)Math.Ceiling(
            options.MinimumSpeechDuration.TotalSeconds * SampleRate));
        var silenceSamples = checked((int)Math.Ceiling(
            options.SilenceTimeout.TotalSeconds * SampleRate));
        var commandBuffer = ArrayPool<byte>.Shared.Rent(maximumSamples * BytesPerSample);
        var analysisBuffer = ArrayPool<byte>.Shared.Rent(AnalysisFrameBytes);
        var analysisLength = 0;
        var commandLength = 0;
        var totalSamples = 0;
        var speechSamples = 0;
        var trailingSilenceSamples = 0;
        var speechStarted = false;
        var completed = false;
        var rmsThreshold = MapThreshold(options.Sensitivity);

        try
        {
            await foreach (var frame in input.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                var source = frame.Pcm16;
                var offset = 0;
                while (offset < source.Length && !completed)
                {
                    var length = Math.Min(AnalysisFrameBytes - analysisLength, source.Length - offset);
                    source.Span.Slice(offset, length).CopyTo(
                        analysisBuffer.AsSpan(analysisLength, length));
                    analysisLength += length;
                    offset += length;

                    if (analysisLength == AnalysisFrameBytes)
                    {
                        completed = ProcessFrame(
                            analysisBuffer.AsSpan(0, analysisLength),
                            rmsThreshold,
                            maximumSamples,
                            silenceSamples,
                            commandBuffer,
                            ref commandLength,
                            ref totalSamples,
                            ref speechSamples,
                            ref trailingSilenceSamples,
                            ref speechStarted);
                        analysisLength = 0;
                    }
                }

                if (completed)
                {
                    break;
                }
            }

            if (!completed && analysisLength > 0)
            {
                if (analysisLength % BytesPerSample != 0)
                {
                    throw UnsupportedFormat("PCM16 microphone stream ended with an incomplete sample.");
                }

                _ = ProcessFrame(
                    analysisBuffer.AsSpan(0, analysisLength),
                    rmsThreshold,
                    maximumSamples,
                    silenceSamples,
                    commandBuffer,
                    ref commandLength,
                    ref totalSamples,
                    ref speechSamples,
                    ref trailingSilenceSamples,
                    ref speechStarted);
            }

            var commandDuration = TimeSpan.FromSeconds(
                commandLength / (double)(SampleRate * BytesPerSample));
            if (!speechStarted || speechSamples < minimumSpeechSamples)
            {
                var observedDuration = speechStarted
                    ? commandDuration
                    : TimeSpan.FromSeconds(totalSamples / (double)SampleRate);
                return new VoiceActivityResult(false, observedDuration, null);
            }

            var captured = commandBuffer.AsSpan(0, commandLength).ToArray();
            var audio = new CapturedCommandAudio(
                captured,
                AudioFormat.Pcm16KhzMono,
                commandDuration);
            return new VoiceActivityResult(true, commandDuration, audio);
        }
        finally
        {
            Array.Clear(commandBuffer);
            Array.Clear(analysisBuffer);
            ArrayPool<byte>.Shared.Return(commandBuffer, clearArray: false);
            ArrayPool<byte>.Shared.Return(analysisBuffer, clearArray: false);
        }
    }

    private static bool ProcessFrame(
        ReadOnlySpan<byte> frame,
        double rmsThreshold,
        int maximumSamples,
        int silenceSamples,
        byte[] commandBuffer,
        ref int commandLength,
        ref int totalSamples,
        ref int speechSamples,
        ref int trailingSilenceSamples,
        ref bool speechStarted)
    {
        var availableSamples = Math.Min(frame.Length / BytesPerSample, maximumSamples - totalSamples);
        if (availableSamples <= 0)
        {
            return true;
        }

        var boundedFrame = frame[..(availableSamples * BytesPerSample)];
        totalSamples += availableSamples;
        var isSpeech = CalculateRms(boundedFrame) >= rmsThreshold;
        if (speechStarted || isSpeech)
        {
            boundedFrame.CopyTo(commandBuffer.AsSpan(commandLength));
            commandLength += boundedFrame.Length;
            speechStarted = true;

            if (isSpeech)
            {
                speechSamples += availableSamples;
                trailingSilenceSamples = 0;
            }
            else
            {
                trailingSilenceSamples += availableSamples;
            }
        }

        return totalSamples >= maximumSamples
            || (speechStarted && trailingSilenceSamples >= silenceSamples);
    }

    private static double CalculateRms(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / BytesPerSample;
        double sumOfSquares = 0;
        for (var offset = 0; offset < pcm16.Length; offset += BytesPerSample)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(
                pcm16.Slice(offset, BytesPerSample)) / 32_768d;
            sumOfSquares += sample * sample;
        }

        return sampleCount == 0 ? 0 : Math.Sqrt(sumOfSquares / sampleCount);
    }

    private static double MapThreshold(double sensitivity)
    {
        var normalized = (sensitivity - MinimumSensitivity)
            / (MaximumSensitivity - MinimumSensitivity);
        return MaximumRmsThreshold
            - (normalized * (MaximumRmsThreshold - MinimumRmsThreshold));
    }

    private static void Validate(
        IAudioCaptureSession input,
        VoiceActivityOptions options)
    {
        if (input.Format != AudioFormat.Pcm16KhzMono)
        {
            throw UnsupportedFormat("Energy VAD requires mono 16 kHz PCM16 audio.");
        }

        if (options.MinimumSpeechDuration <= TimeSpan.Zero
            || options.SilenceTimeout <= TimeSpan.Zero
            || options.MaximumCommandDuration <= TimeSpan.Zero
            || options.MinimumSpeechDuration > options.MaximumCommandDuration
            || options.SilenceTimeout > options.MaximumCommandDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Voice activity durations must be positive and bounded by the command limit.");
        }

        if (!double.IsFinite(options.Sensitivity)
            || options.Sensitivity is < MinimumSensitivity or > MaximumSensitivity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Sensitivity,
                "Voice sensitivity must be between 0.65 and 0.90.");
        }
    }

    private static AudioCaptureException UnsupportedFormat(string message) =>
        new(AudioInputResultCode.UnsupportedFormat, message);
}
