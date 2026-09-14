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

        bool xpSeen = false, rawSeen = false;
        foreach (var row in parser.GroupIntoRows(words))
        {
            if (row.Any(w => w.Confidence is < 50)) continue;
            for (int split = 1; split < row.Count; split++)
            {
                string label = Normalize(string.Join(" ", row.Take(split).Select(w => w.Text)));
                bool raw = label.StartsWith("raw") || label.StartsWith("ram") || label.StartsWith("ravv");
                string rateLabel = raw ? label[(label.StartsWith("ravv") ? 4 : 3)..] : label;
                if (rateLabel is not ("xph" or "xpfh" or "xpsh" or "xpih" or "xpth" or "xphour")) continue;
                // Do not join values belonging to neighboring widgets on the same Y.
                if (row[split].X < row[split - 1].Right) break;
                if (Enumerable.Range(split + 1, row.Count - split - 1).Any(i =>
                    row[i].X - row[i - 1].Right > Math.Max(12, row[i].Height * 2))) break;
                // Parse the entire numeric span, never just its rightmost fragment.
                string numeric = string.Concat(row.Skip(split).Select(w => w.Text));
                if (!System.Text.RegularExpressions.Regex.IsMatch(numeric, @"^[0-9OoIl,\.]+(?:[kKmM]{1,2})?$")) break;
                var value = parser.ParseNumber(numeric);
                if (!value.HasValue || value < 0) break;
                if (raw)
                {
                    rawXpPerHour = rawSeen ? null : value;
                    rawSeen = true;
                }
                else
                {
                    xpPerHour = xpSeen ? null : value;
                    xpSeen = true;
                }
                break;
            }
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
            XpRateSource = rates.XpPerHour.HasValue ? "xp-analyser" : snapshot.XpRateSource,
            RawXpRateSource = rates.RawXpPerHour.HasValue ? "xp-analyser" : snapshot.RawXpRateSource,
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

    private static string Normalize(string text) => new(
        text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
