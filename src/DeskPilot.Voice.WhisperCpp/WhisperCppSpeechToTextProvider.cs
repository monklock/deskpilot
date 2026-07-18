using System.Buffers.Binary;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.WhisperCpp;

/// <summary>Recognizes bounded Russian command audio through local Whisper.cpp.</summary>
public sealed class WhisperCppSpeechToTextProvider(
    string modelPath,
    IWhisperClientFactory clients) : ISpeechToTextProvider
{
    private readonly string _modelPath = !string.IsNullOrWhiteSpace(modelPath)
        ? modelPath
        : throw new ArgumentException("The Whisper model path is required.", nameof(modelPath));
    private readonly IWhisperClientFactory _clients =
        clients ?? throw new ArgumentNullException(nameof(clients));

    /// <inheritdoc />
    public string ProviderId => "whisper.cpp";

    /// <inheritdoc />
    public async Task<SpeechRecognitionResult> RecognizeAsync(
        CapturedCommandAudio audio,
        SpeechRecognitionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAudio(audio);
        ValidateOptions(options);
        if (!File.Exists(_modelPath))
        {
            throw Failure(
                SpeechRecognitionFailureCode.ModelUnavailable,
                "The active Whisper model is unavailable.");
        }

        IWhisperClient client;
        try
        {
            client = _clients.Create(_modelPath);
        }
        catch (Exception exception)
        {
            throw Failure(
                SpeechRecognitionFailureCode.ModelUnavailable,
                "The active Whisper model could not be loaded.",
                exception);
        }

        await using (client.ConfigureAwait(false))
        {
            try
            {
                var samples = ConvertSamples(audio.Pcm16.Span);
                var segments = new List<WhisperSegment>();
                await foreach (var segment in client.ProcessAsync(
                    samples,
                    "ru",
                    cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        if (!double.IsFinite(segment.Confidence)
                            || segment.Confidence is < 0 or > 1)
                        {
                            throw Failure(
                                SpeechRecognitionFailureCode.ProviderFailure,
                                "Whisper returned invalid segment confidence.");
                        }

                        segments.Add(segment);
                    }
                }

                if (segments.Count == 0)
                {
                    throw Failure(
                        SpeechRecognitionFailureCode.NoText,
                        "Whisper returned no recognized command text.");
                }

                var text = string.Join(
                    " ",
                    segments.Select(segment => segment.Text.Trim()));
                var confidence = segments.Average(segment => segment.Confidence);
                if (confidence < options.MinimumConfidence)
                {
                    throw Failure(
                        SpeechRecognitionFailureCode.ConfidenceBelowThreshold,
                        "Whisper recognition confidence is below the configured threshold.");
                }

                return new SpeechRecognitionResult(text, confidence, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SpeechRecognitionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw Failure(
                    SpeechRecognitionFailureCode.ProviderFailure,
                    "Whisper failed to recognize the local command audio.",
                    exception);
            }
        }
    }

    private static float[] ConvertSamples(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                pcm16.Slice(index * sizeof(short), sizeof(short))) / 32_768f;
        }

        return samples;
    }

    private static void ValidateAudio(CapturedCommandAudio audio)
    {
        if (audio.Format != AudioFormat.Pcm16KhzMono
            || audio.Pcm16.Length % sizeof(short) != 0)
        {
            throw Failure(
                SpeechRecognitionFailureCode.UnsupportedFormat,
                "Whisper requires mono 16 kHz PCM16 command audio.");
        }
    }

    private static void ValidateOptions(SpeechRecognitionOptions options)
    {
        if (!double.IsFinite(options.MinimumConfidence)
            || options.MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinimumConfidence,
                "Speech confidence must be between zero and one.");
        }
    }

    private static SpeechRecognitionException Failure(
        SpeechRecognitionFailureCode code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
