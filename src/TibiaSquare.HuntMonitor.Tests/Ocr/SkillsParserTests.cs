using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Ocr;
using TibiaSquare.HuntMonitor.Private;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Ocr;

public class SkillsParserTests
{
    private static readonly IHuntAnalyserParser Parser = new RowParser();

    [Theory]
    [InlineData("56:56h", 3416)]
    [InlineData("5:30h", 330)]
    [InlineData("100:00h", 6000)]
    [InlineData("0:05h", 5)]
    [InlineData("1:23h", 83)]
    [InlineData("42:00h", 2520)]
    public void ParseStamina_ValidFormats(string timeValue, int expectedMinutes)
    {
        var words = new List<OcrWordInfo>
        {
            new("Stamina", 10, 50, 60, 12),
            new(timeValue, 120, 50, 50, 12),
        };
        Assert.Equal(expectedMinutes, SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_TrailingNoise()
    {
        var words = new List<OcrWordInfo>
        {
            new("Stamina", 10, 50, 60, 12),
            new("56:56h!_", 120, 50, 55, 12),
        };
        Assert.Equal(3416, SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_MisreadDigits()
    {
        var words = new List<OcrWordInfo>
        {
            new("Stamina", 10, 50, 60, 12),
            new("O5:OOh", 120, 50, 50, 12),
        };
        Assert.Equal(300, SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_ColonAsSpace()
    {
        var words = new List<OcrWordInfo>
        {
            new("Stamina", 10, 50, 60, 12),
            new("56 56h", 120, 50, 50, 12),
        };
        Assert.Equal(3416, SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_NoStaminaLabel_ReturnsNull()
    {
        var words = new List<OcrWordInfo>
        {
            new("Sword", 10, 50, 40, 12),
            new("110", 120, 50, 30, 12),
        };
        Assert.Null(SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_EmptyWords_ReturnsNull()
    {
        Assert.Null(SkillsParser.ParseStamina(Parser, []));
    }

    [Fact]
    public void ParseStamina_MultipleRows_FindsStaminaRow()
    {
        var words = new List<OcrWordInfo>
        {
            new("Sword", 10, 10, 40, 12),
            new("Fighting", 55, 10, 50, 12),
            new("110", 150, 10, 30, 12),
            new("Shielding", 10, 30, 60, 12),
            new("108", 150, 30, 30, 12),
            new("Stamina", 10, 50, 60, 12),
            new("42:00h", 120, 50, 50, 12),
        };
        Assert.Equal(2520, SkillsParser.ParseStamina(Parser, words));
    }

    [Fact]
    public void ParseStamina_StaminaWithoutTrailingH()
    {
        var words = new List<OcrWordInfo>
        {
            new("Stamina", 10, 50, 60, 12),
            new("56:56", 120, 50, 50, 12),
        };
        Assert.Equal(3416, SkillsParser.ParseStamina(Parser, words));
    }

    private sealed class RowParser : IHuntAnalyserParser
    {
        public List<List<OcrWordInfo>> GroupIntoRows(IReadOnlyList<OcrWordInfo> words)
        {
            return words
                .GroupBy(w => Math.Round(w.Y / 10.0))
                .OrderBy(g => g.Min(w => w.Y))
                .Select(g => g.OrderBy(w => w.X).ToList())
                .ToList();
        }

        public HuntSnapshot? ParseSpatial(IReadOnlyList<OcrWordInfo> words) => throw new NotSupportedException();
        public HuntSnapshot? Parse(IReadOnlyList<string> lines) => throw new NotSupportedException();
        public double? FindValueColumnX(List<List<OcrWordInfo>> rows) => throw new NotSupportedException();
        public IReadOnlyList<string> ReconstructLinesColumnAware(List<List<OcrWordInfo>> rows, double valueColumnX) => throw new NotSupportedException();
        public IReadOnlyList<string> ReconstructLines(IReadOnlyList<OcrWordInfo> words) => throw new NotSupportedException();
        public bool IsNumericWord(string text) => throw new NotSupportedException();
        public long? ParseNumber(string input) => throw new NotSupportedException();
        public TimeSpan? ParseSessionTime(string input) => throw new NotSupportedException();
    }
}
