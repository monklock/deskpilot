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

            return new VoskRecognition(
                text,
                isFinal ? ReadMinimumConfidence(root) : 0,
                isFinal);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "Vosk returned invalid recognition JSON.",
                exception);
        }
    }

    private static double ReadMinimumConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return 0;
        }

        var minimum = double.MaxValue;
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("conf", out var confidenceElement)
                || !confidenceElement.TryGetDouble(out var confidence)
                || !double.IsFinite(confidence)
                || confidence is < 0 or > 1)
            {
                return 0;
            }

            minimum = Math.Min(minimum, confidence);
        }

        return minimum;
    }
}
