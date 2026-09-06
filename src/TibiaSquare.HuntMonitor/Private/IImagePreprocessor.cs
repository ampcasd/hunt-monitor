using Windows.Graphics.Imaging;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Live-tunable preprocessing values shared by every OCR panel. Replacing the whole
/// record keeps capture-thread reads consistent while the debug UI is being adjusted.
/// </summary>
public sealed record ImagePreprocessingSettings
{
    public long Revision { get; init; }
    public int UpscaleFactor { get; init; } = 4;
    public int BinarizationThreshold { get; init; } = 107;
    public int SmoothingRadius { get; init; } = 2;
    public int DilationRadius { get; init; } = 1;
    public int DotEnhancementRadius { get; init; }
    public int MinBlobArea { get; init; }
    public bool EnableColorEnhancement { get; init; } = true;
    public bool EnableCoinBlanking { get; init; } = true;
    public bool EnableBottomBorderCrop { get; init; } = true;
    public bool EnableSmallBlobRemoval { get; init; } = true;
}

/// <summary>
/// Image preprocessing for OCR — contrast stretch, denoise, binarize, border crop.
/// Implemented by the Processing DLL.
/// </summary>
public interface IImagePreprocessor
{
    ImagePreprocessingSettings Settings { get; set; }
    ImagePreprocessingSettings LastAppliedSettings { get; }

    byte[] PreprocessForOcr(SoftwareBitmap bitmap, bool detectBottomBorder = true);
}
