using System.Text.Json;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.GigaStt;

internal static class GigaSttProtocol
{
    internal const int MaximumResponseBytes = 1_048_576;

    internal static string Normalize(string text) => string.Join(' ',
        new string(text.Where(character => !char.IsPunctuation(character)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    internal static bool Number(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value) && double.IsFinite(value);
    }

    internal static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;

    internal static WakeWordDetectionResult? Detection(
        JsonElement root, WakeWordOptions options, long origin, long consumed)
    {
        if (Text(root, "type") != "final" || !root.TryGetProperty("is_final", out var final)
            || final.ValueKind != JsonValueKind.True
            || Normalize(Text(root, "text")) != Normalize(options.Phrase))
        {
            return null;
        }

        if (!Number(root, "confidence", out var confidence) || confidence < options.MinimumConfidence
            || confidence is < 0 or > 1)
        {
            return null;
        }

        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array
            || words.GetArrayLength() != 1 || Normalize(Text(words[0], "word")) != Normalize(options.Phrase)
            || !Number(words[0], "confidence", out var wordConfidence) || wordConfidence is < 0 or > 1
            || !Number(words[0], "start", out var start) || !Number(words[0], "end", out var end)
            || start < 0 || end <= start || end > (consumed - origin) / 16_000d)
        {
            throw new WakeWordDetectionException(WakeWordDetectionFailureCode.InvalidTiming,
                "GigaSTT вернул некорректные границы ключевой фразы.");
        }

        return new WakeWordDetectionResult(options.Phrase, confidence,
            checked(origin + (long)Math.Round(start * 16_000, MidpointRounding.AwayFromZero)),
            checked(origin + (long)Math.Round(end * 16_000, MidpointRounding.AwayFromZero)), consumed);
    }

    internal static SpeechRecognitionResult Recognition(JsonElement root, double threshold)
    {
        var text = Text(root, "text").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechRecognitionException(SpeechRecognitionFailureCode.NoText,
                "GigaSTT не распознал команду.");
        }

        if (!Number(root, "confidence", out var confidence) || confidence is < 0 or > 1)
        {
            throw new SpeechRecognitionException(SpeechRecognitionFailureCode.ProviderFailure,
                "GigaSTT не вернул корректную оценку уверенности.");
        }

        if (confidence < threshold)
        {
            throw new SpeechRecognitionException(SpeechRecognitionFailureCode.ConfidenceBelowThreshold,
                "Уверенность распознавания GigaSTT ниже порога.")
            { RecognizedText = text, RecognitionConfidence = confidence };
        }

        return new SpeechRecognitionResult(text, confidence, true);
    }

    internal static async Task<JsonDocument> ReadJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("GigaSTT response exceeds the size limit.");
            }

            buffer.Write(chunk, 0, count);
        }

        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
