using DeskPilot.Voice.Abstractions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DeskPilot.Voice.Vosk;

/// <summary>Detects one configured wake phrase with a limited Vosk grammar.</summary>
public sealed class VoskWakeWordProvider(
    string modelPath,
    IVoskRecognizerClientFactory recognizers) : IWakeWordProvider
{
    private static readonly JsonSerializerOptions GrammarJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
    };
    private readonly string _modelPath = !string.IsNullOrWhiteSpace(modelPath)
        ? modelPath
        : throw new ArgumentException("The Vosk model path is required.", nameof(modelPath));
    private readonly IVoskRecognizerClientFactory _recognizers =
        recognizers ?? throw new ArgumentNullException(nameof(recognizers));

    /// <inheritdoc />
    public string ProviderId => "vosk";

    /// <inheritdoc />
    public async Task<WakeWordDetectionResult> WaitForDetectionAsync(
        IAudioCaptureSession audio,
        WakeWordOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Format != AudioFormat.Pcm16KhzMono)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Vosk требуется монофонический PCM16 поток с частотой 16 кГц.");
        }

        var phrase = options.Phrase?.Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            throw new ArgumentException("Wake phrase is required.", nameof(options));
        }

        if (!double.IsFinite(options.MinimumConfidence)
            || options.MinimumConfidence is < 0.65 or > 0.90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinimumConfidence,
                "Wake confidence must be between 0.65 and 0.90.");
        }

        var grammarJson = JsonSerializer.Serialize(new[] { phrase }, GrammarJsonOptions);
        using var recognizer = _recognizers.Create(_modelPath, grammarJson);
        await foreach (var frame in audio.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognition = recognizer.Accept(frame.Pcm16);
            if (TryCreateDetection(
                recognition,
                phrase,
                options.MinimumConfidence,
                out var detection))
            {
                return detection;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryCreateDetection(
            recognizer.Complete(),
            phrase,
            options.MinimumConfidence,
            out var completedDetection))
        {
            return completedDetection;
        }

        throw new EndOfStreamException("Поток микрофона завершён до обнаружения ключевой фразы.");
    }

    private static bool TryCreateDetection(
        VoskRecognition recognition,
        string phrase,
        double minimumConfidence,
        out WakeWordDetectionResult detection)
    {
        if (recognition.IsFinal
            && recognition.Confidence >= minimumConfidence
            && string.Equals(
                recognition.Text?.Trim(),
                phrase,
                StringComparison.OrdinalIgnoreCase))
        {
            detection = new WakeWordDetectionResult(phrase, recognition.Confidence);
            return true;
        }

        detection = default!;
        return false;
    }
}
