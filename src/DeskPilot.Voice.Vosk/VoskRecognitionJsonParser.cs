using System.Text.Json;

namespace DeskPilot.Voice.Vosk;

internal static class VoskRecognitionJsonParser
{
    public static VoskRecognition Parse(string json, bool isFinal)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var textProperty = isFinal ? "text" : "partial";
            var text = root.TryGetProperty(textProperty, out var textElement)
                && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;

            var words = isFinal ? ReadWords(root) : [];
            return new VoskRecognition(
                text,
                words.Count > 0 ? words.Min(word => word.Confidence) : 0,
                isFinal,
                words);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "Vosk returned invalid recognition JSON.",
                exception);
        }
    }

    private static IReadOnlyList<VoskWordTiming> ReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return [];
        }

        var words = new List<VoskWordTiming>(results.GetArrayLength());
        double? previousEnd = null;
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("word", out var wordElement)
                || wordElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(wordElement.GetString())
                || !result.TryGetProperty("conf", out var confidenceElement)
                || !confidenceElement.TryGetDouble(out var confidence)
                || !double.IsFinite(confidence)
                || confidence is < 0 or > 1
                || !result.TryGetProperty("start", out var startElement)
                || !startElement.TryGetDouble(out var startSeconds)
                || !double.IsFinite(startSeconds)
                || startSeconds < 0
                || !result.TryGetProperty("end", out var endElement)
                || !endElement.TryGetDouble(out var endSeconds)
                || !double.IsFinite(endSeconds)
                || endSeconds < 0
                || startSeconds >= endSeconds
                || previousEnd is double precedingEnd && startSeconds < precedingEnd
                || !TryCreateTimeSpan(startSeconds, out var start)
                || !TryCreateTimeSpan(endSeconds, out var end)
                || start >= end)
            {
                return [];
            }

            words.Add(new VoskWordTiming(wordElement.GetString()!, confidence, start, end));
            previousEnd = endSeconds;
        }

        return words;
    }

    private static bool TryCreateTimeSpan(double seconds, out TimeSpan result)
    {
        if (seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            result = default;
            return false;
        }

        result = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
