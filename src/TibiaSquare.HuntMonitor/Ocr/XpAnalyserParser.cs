using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Private;

namespace TibiaSquare.HuntMonitor.Ocr;

/// <summary>
/// Parses the rolling XP/h and Raw XP/h rows from the XP Analyser widget.
/// </summary>
public static class XpAnalyserParser
{
    public static XpAnalyserRates? Parse(
        IHuntAnalyserParser parser,
        IReadOnlyList<OcrWordInfo> words)
    {
        long? xpPerHour = null;
        long? rawXpPerHour = null;

        foreach (var row in parser.GroupIntoRows(words))
        {
            string rowText = string.Join(" ", row.Select(word => word.Text));
            if (!LooksLikeXpRate(rowText))
                continue;

            long? value = ParseRightmostValue(parser, row);
            if (!value.HasValue || value.Value < 0)
                continue;

            if (LooksLikeRawRate(rowText))
                rawXpPerHour = value;
            else
                xpPerHour = value;
        }

        var result = new XpAnalyserRates
        {
            XpPerHour = xpPerHour,
            RawXpPerHour = rawXpPerHour
        };
        return result.HasAnyRate ? result : null;
    }

    public static HuntSnapshot ApplyRates(HuntSnapshot snapshot, XpAnalyserRates? rates)
    {
        if (rates is not { HasAnyRate: true })
            return snapshot;

        return snapshot with
        {
            XpPerHour = rates.XpPerHour ?? snapshot.XpPerHour,
            RawXpPerHour = rates.RawXpPerHour ?? snapshot.RawXpPerHour
        };
    }

    public static bool LooksLikeXpRate(string text)
    {
        string normalized = Normalize(text);
        if (!normalized.Contains("xp", StringComparison.Ordinal))
            return false;

        return normalized.Contains("xph", StringComparison.Ordinal)
            || normalized.Contains("xpfh", StringComparison.Ordinal)
            || normalized.Contains("xpsh", StringComparison.Ordinal)
            || normalized.Contains("xpih", StringComparison.Ordinal)
            || normalized.Contains("xpth", StringComparison.Ordinal)
            || normalized.Contains("xphour", StringComparison.Ordinal);
    }

    public static bool LooksLikeRawRate(string text)
    {
        string normalized = Normalize(text);
        return normalized.Contains("rawxp", StringComparison.Ordinal)
            || normalized.Contains("ramxp", StringComparison.Ordinal)
            || normalized.Contains("ravvxp", StringComparison.Ordinal);
    }

    private static long? ParseRightmostValue(
        IHuntAnalyserParser parser,
        IReadOnlyList<OcrWordInfo> row)
    {
        for (int i = row.Count - 1; i >= 0; i--)
        {
            var parsed = parser.ParseNumber(row[i].Text);
            if (parsed.HasValue)
                return parsed;

            if (i > 0)
            {
                parsed = parser.ParseNumber(row[i - 1].Text + row[i].Text);
                if (parsed.HasValue)
                    return parsed;
            }
        }

        return null;
    }

    private static string Normalize(string text) => new(
        text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
