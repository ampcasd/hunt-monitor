using System.Text.RegularExpressions;
using TibiaSquare.HuntMonitor.Private;

namespace TibiaSquare.HuntMonitor.Ocr;

/// <summary>
/// Parses the stamina value from OCR words of the Skills widget.
/// </summary>
public static partial class SkillsParser
{
    // Matches stamina format: HH:MMh where HH is 1-3 digits.
    // Tesseract may read ":" as " " so accept both separators.
    // Trailing "h" may be dropped or followed by noise.
    [GeneratedRegex(@"^(\d{1,3})[:\s](\d{2})h?[!\.\s]*$", RegexOptions.Compiled)]
    private static partial Regex StaminaTimeRegex();

    /// <summary>
    /// Parses the stamina value from OCR words of the Skills widget.
    /// Returns stamina in total minutes, or null if not found/unparseable.
    /// </summary>
    public static int? ParseStamina(IHuntAnalyserParser parser, IReadOnlyList<OcrWordInfo> words)
    {
        var rows = parser.GroupIntoRows(words);

        foreach (var row in rows)
        {
            var rowText = string.Join(" ", row.Select(w => w.Text));

            if (!rowText.Contains("Stamina", StringComparison.OrdinalIgnoreCase))
                continue;

            // Strategy 1: try each word right-to-left for a direct match
            for (int i = row.Count - 1; i >= 0; i--)
            {
                var result = TryParseTime(row[i].Text);
                if (result.HasValue)
                    return result.Value;
            }

            // Strategy 2: join row text and extract time after "Stamina"
            var joined = string.Join("", row.Select(w => w.Text));
            var staminaIdx = joined.IndexOf("Stamina", StringComparison.OrdinalIgnoreCase);
            if (staminaIdx >= 0)
            {
                var afterLabel = joined[(staminaIdx + 7)..].Trim();
                var result = TryParseTime(afterLabel);
                if (result.HasValue)
                    return result.Value;
            }

            // Strategy 3: fix OCR misreads (O→0, l→1, I→1) and retry
            for (int i = row.Count - 1; i >= 0; i--)
            {
                var fixed_ = row[i].Text
                    .Replace("O", "0").Replace("o", "0")
                    .Replace("l", "1").Replace("I", "1");
                var result = TryParseTime(fixed_);
                if (result.HasValue)
                    return result.Value;
            }

            break; // only check the first Stamina row
        }

        return null;
    }

    private static int? TryParseTime(string text)
    {
        var cleaned = StripTrailingNoise(text);
        var match = StaminaTimeRegex().Match(cleaned);
        if (!match.Success)
            return null;

        int hours = int.Parse(match.Groups[1].Value);
        int minutes = int.Parse(match.Groups[2].Value);
        return hours * 60 + minutes;
    }

    private static string StripTrailingNoise(string text)
    {
        var s = text;
        while (s.Length > 0 && !char.IsLetterOrDigit(s[^1]))
            s = s[..^1];
        return s;
    }
}
