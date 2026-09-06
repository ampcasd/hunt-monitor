using System.Globalization;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Ocr;
using TibiaSquare.HuntMonitor.Private;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Ocr;

public sealed class XpAnalyserParserTests
{
    private static readonly IHuntAnalyserParser Parser = new RowParser();

    [Fact]
    public void Parse_ReadsBothRollingRates()
    {
        OcrWordInfo[] words =
        [
            new("XP/h:", 8, 40, 42, 12),
            new("6,312,450", 92, 40, 70, 12),
            new("Raw", 8, 60, 25, 12),
            new("XP/h:", 36, 60, 42, 12),
            new("3,942,100", 92, 60, 70, 12)
        ];

        var result = XpAnalyserParser.Parse(Parser, words);

        Assert.NotNull(result);
        Assert.Equal(6_312_450, result.XpPerHour);
        Assert.Equal(3_942_100, result.RawXpPerHour);
    }

    [Fact]
    public void Parse_HandlesObservedLabelGlyphConfusionsAndSuffix()
    {
        OcrWordInfo[] words =
        [
            new("xPfh:", 8, 40, 42, 12),
            new("6.3kk", 92, 40, 48, 12),
            new("Ram", 8, 60, 28, 12),
            new("xPfh:", 38, 60, 42, 12),
            new("3.9kk", 92, 60, 48, 12)
        ];

        var result = XpAnalyserParser.Parse(Parser, words);

        Assert.Equal(6_300_000, result?.XpPerHour);
        Assert.Equal(3_900_000, result?.RawXpPerHour);
    }

    [Fact]
    public void Parse_DoesNotTreatXpGainAsRate()
    {
        OcrWordInfo[] words =
        [
            new("XP", 8, 40, 20, 12),
            new("Gain:", 32, 40, 34, 12),
            new("3,942,100", 92, 40, 70, 12)
        ];

        Assert.Null(XpAnalyserParser.Parse(Parser, words));
    }

    [Fact]
    public void Parse_UsesAvailableRateWhenTheOtherRowIsUnreadable()
    {
        OcrWordInfo[] words =
        [
            new("Raw", 8, 60, 25, 12),
            new("XP/h:", 36, 60, 42, 12),
            new("4,200,000", 92, 60, 70, 12)
        ];

        var result = XpAnalyserParser.Parse(Parser, words);

        Assert.Null(result?.XpPerHour);
        Assert.Equal(4_200_000, result?.RawXpPerHour);
    }

    [Fact]
    public void ApplyRates_OverridesOnlyRatesReadFromXpAnalyser()
    {
        var huntSnapshot = new HuntSnapshot
        {
            XpPerHour = 6_300_000,
            RawXpPerHour = 3_900_000,
            XpGain = 1_000
        };
        var xpAnalyserRates = new XpAnalyserRates
        {
            XpPerHour = 7_100_000
        };

        var result = XpAnalyserParser.ApplyRates(huntSnapshot, xpAnalyserRates);

        Assert.Equal(7_100_000, result.XpPerHour);
        Assert.Equal(3_900_000, result.RawXpPerHour);
        Assert.Equal(1_000, result.XpGain);
    }

    private sealed class RowParser : IHuntAnalyserParser
    {
        public List<List<OcrWordInfo>> GroupIntoRows(IReadOnlyList<OcrWordInfo> words) => words
            .GroupBy(word => Math.Round(word.Y / 10.0))
            .OrderBy(group => group.Min(word => word.Y))
            .Select(group => group.OrderBy(word => word.X).ToList())
            .ToList();

        public long? ParseNumber(string input)
        {
            string value = input.Trim().Replace(",", string.Empty).ToLowerInvariant();
            long multiplier = 1;
            if (value.EndsWith("kk", StringComparison.Ordinal))
            {
                multiplier = 1_000_000;
                value = value[..^2];
            }
            else if (value.EndsWith('k'))
            {
                multiplier = 1_000;
                value = value[..^1];
            }

            return double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out double parsed)
                ? (long)Math.Round(parsed * multiplier)
                : null;
        }

        public HuntSnapshot? ParseSpatial(IReadOnlyList<OcrWordInfo> words) => throw new NotSupportedException();
        public HuntSnapshot? Parse(IReadOnlyList<string> lines) => throw new NotSupportedException();
        public double? FindValueColumnX(List<List<OcrWordInfo>> rows) => throw new NotSupportedException();
        public IReadOnlyList<string> ReconstructLinesColumnAware(List<List<OcrWordInfo>> rows, double valueColumnX) => throw new NotSupportedException();
        public IReadOnlyList<string> ReconstructLines(IReadOnlyList<OcrWordInfo> words) => throw new NotSupportedException();
        public bool IsNumericWord(string text) => throw new NotSupportedException();
        public TimeSpan? ParseSessionTime(string input) => throw new NotSupportedException();
    }
}
