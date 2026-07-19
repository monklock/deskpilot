using System.Globalization;

namespace DeskPilot.Application.Intents;

/// <summary>Parses safe Russian cardinal volume values in the inclusive 0..100 range.</summary>
public sealed class RussianVolumeNumberParser
{
    private static readonly IReadOnlyDictionary<string, int> Single =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ноль"] = 0,
            ["один"] = 1,
            ["два"] = 2,
            ["три"] = 3,
            ["четыре"] = 4,
            ["пять"] = 5,
            ["шесть"] = 6,
            ["семь"] = 7,
            ["восемь"] = 8,
            ["девять"] = 9,
            ["десять"] = 10,
            ["одиннадцать"] = 11,
            ["двенадцать"] = 12,
            ["тринадцать"] = 13,
            ["четырнадцать"] = 14,
            ["пятнадцать"] = 15,
            ["шестнадцать"] = 16,
            ["семнадцать"] = 17,
            ["восемнадцать"] = 18,
            ["девятнадцать"] = 19,
            ["двадцать"] = 20,
            ["тридцать"] = 30,
            ["сорок"] = 40,
            ["пятьдесят"] = 50,
            ["шестьдесят"] = 60,
            ["семьдесят"] = 70,
            ["восемьдесят"] = 80,
            ["девяносто"] = 90,
            ["сто"] = 100,
        };

    /// <summary>Attempts to parse one complete numeric phrase without guessing.</summary>
    public bool TryParse(string input, out int value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (int.TryParse(
                normalized,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
        {
            return value is >= 0 and <= 100;
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return Single.TryGetValue(parts[0], out value);
        }

        if (parts.Length != 2
            || !Single.TryGetValue(parts[0], out var tens)
            || tens is < 20 or > 90
            || tens % 10 != 0
            || !Single.TryGetValue(parts[1], out var units)
            || units is < 1 or > 9)
        {
            value = default;
            return false;
        }

        value = tens + units;
        return true;
    }
}
