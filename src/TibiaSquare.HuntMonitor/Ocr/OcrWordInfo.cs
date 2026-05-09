namespace TibiaSquare.HuntMonitor.Ocr;

/// <summary>
/// Represents a single word recognized by OCR with its spatial position.
/// Used for spatial grouping to correctly associate labels with values.
/// </summary>
public record OcrWordInfo(string Text, double X, double Y, double Width, double Height)
{
    public double CenterY => Y + Height / 2;
    public double Right => X + Width;
}
