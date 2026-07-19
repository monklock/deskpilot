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
    private const int SamplesPerSecond = 16_000;
    private const string InvalidWakeTimingMessage = "Vosk returned invalid wake-word timing.";
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

    /// <summary>Waits for a timed wake phrase on a sequenced buffered-audio cursor.</summary>
    public async Task<WakeWordDetectionResult> WaitForDetectionAsync(
        IVoiceAudioCursor audio,
        WakeWordOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var phrase = ValidateInput(audio.Format, options, cancellationToken);
        if (audio.StartSampleOffset < 0)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage);
        }

        var grammarJson = JsonSerializer.Serialize(new[] { phrase }, GrammarJsonOptions);
        using var recognizer = _recognizers.Create(_modelPath, grammarJson);
        var detectionSampleOffset = audio.StartSampleOffset;
        await foreach (var frame in audio.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFrame(frame, detectionSampleOffset);
            detectionSampleOffset = frame.EndSampleOffset;
            var recognition = recognizer.Accept(frame.Pcm16);
            if (TryCreateTimedDetection(
                recognition,
                phrase,
                options.MinimumConfidence,
                audio.StartSampleOffset,
                detectionSampleOffset,
                out var detection))
            {
                return detection;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryCreateTimedDetection(
            recognizer.Complete(),
            phrase,
            options.MinimumConfidence,
            audio.StartSampleOffset,
            detectionSampleOffset,
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

    private static string ValidateInput(
        AudioFormat format,
        WakeWordOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (format != AudioFormat.Pcm16KhzMono)
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

        return phrase;
    }

    private static void ValidateFrame(SequencedAudioFrame frame, long expectedStartSampleOffset)
    {
        if (frame.StartSampleOffset != expectedStartSampleOffset
            || frame.EndSampleOffset <= frame.StartSampleOffset)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage);
        }
    }

    private static bool TryCreateTimedDetection(
        VoskRecognition recognition,
        string phrase,
        double minimumConfidence,
        long cursorStartSampleOffset,
        long detectionSampleOffset,
        out WakeWordDetectionResult detection)
    {
        detection = default!;
        if (!recognition.IsFinal
            || !string.Equals(
                recognition.Text?.Trim(),
                phrase,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!double.IsFinite(recognition.Confidence)
            || recognition.Confidence is < 0 or > 1)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage);
        }

        if (recognition.Confidence < minimumConfidence)
        {
            return false;
        }

        if (recognition.Words is null
            || recognition.Words.Count != 1
            || !string.Equals(
                recognition.Words[0].Word,
                phrase,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(InvalidWakeTimingMessage);
        }

        var word = recognition.Words[0];
        if (!double.IsFinite(word.Confidence)
            || word.Confidence is < 0 or > 1
            || word.Confidence != recognition.Confidence
            || word.Start < TimeSpan.Zero
            || word.End <= word.Start)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage);
        }

        try
        {
            var wakeStart = checked(
                cursorStartSampleOffset
                + checked((long)Math.Round(
                    word.Start.TotalSeconds * SamplesPerSecond,
                    MidpointRounding.AwayFromZero)));
            var wakeEnd = checked(
                cursorStartSampleOffset
                + checked((long)Math.Round(
                    word.End.TotalSeconds * SamplesPerSecond,
                    MidpointRounding.AwayFromZero)));

            if (wakeStart < cursorStartSampleOffset
                || wakeEnd <= wakeStart
                || wakeEnd > detectionSampleOffset)
            {
                throw new InvalidDataException(InvalidWakeTimingMessage);
            }

            detection = new WakeWordDetectionResult(
                phrase,
                recognition.Confidence,
                wakeStart,
                wakeEnd,
                detectionSampleOffset);
            return true;
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage, exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(InvalidWakeTimingMessage, exception);
        }
    }
}
