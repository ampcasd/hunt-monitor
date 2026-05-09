using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Ocr;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Parses Hunt Analyser OCR output into structured snapshots.
/// Implemented by the Processing DLL.
/// </summary>
public interface IHuntAnalyserParser
{
    HuntSnapshot? ParseSpatial(IReadOnlyList<OcrWordInfo> words);
    HuntSnapshot? Parse(IReadOnlyList<string> lines);
    List<List<OcrWordInfo>> GroupIntoRows(IReadOnlyList<OcrWordInfo> words);
    double? FindValueColumnX(List<List<OcrWordInfo>> rows);
    IReadOnlyList<string> ReconstructLinesColumnAware(List<List<OcrWordInfo>> rows, double valueColumnX);
    IReadOnlyList<string> ReconstructLines(IReadOnlyList<OcrWordInfo> words);
    bool IsNumericWord(string text);
    long? ParseNumber(string input);
    TimeSpan? ParseSessionTime(string input);
}
