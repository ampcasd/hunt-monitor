using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Ocr;
using TibiaSquare.HuntMonitor.Private;

namespace TibiaSquare.HuntMonitor.Tray;

public partial class DebugOverlayWindow : Window
{
    private readonly IHuntAnalyserParser _parser;
    private DateTime _lastImageUpdate = DateTime.MinValue;
    private DateTime _lastSkillsImageUpdate = DateTime.MinValue;
    private static readonly TimeSpan ImageRefreshInterval = TimeSpan.FromSeconds(3);

    private static readonly string BoundsFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare", "debug-overlay-bounds.json");

    public DebugOverlayWindow(IHuntAnalyserParser parser)
    {
        _parser = parser;
        InitializeComponent();
        RestoreWindowBounds();
        LocationChanged += (_, _) => SaveWindowBounds();
        SizeChanged += (_, _) => SaveWindowBounds();
        StateChanged += (_, _) => SaveWindowBounds();
    }

    public void UpdateData(HuntSnapshot? snapshot, IReadOnlyList<string> ocrLines,
        IReadOnlyList<OcrWordInfo> words, double? valueColumnX, byte[]? preprocessedPng = null,
        string? regionDebug = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateData(snapshot, ocrLines, words, valueColumnX, preprocessedPng, regionDebug));
            return;
        }

        // Status
        var status = snapshot != null
            ? $"{words.Count} words → {ocrLines.Count} lines → parsed OK   colX={valueColumnX?.ToString("F0") ?? "?"}"
            : $"{words.Count} words → {ocrLines.Count} lines → no session time";
        if (regionDebug != null)
            status += $"   [{regionDebug}]";
        StatusText.Text = status;

        // Parsed values
        if (snapshot != null)
        {
            ValSessionTime.Text = snapshot.SessionTime.ToString(@"hh\:mm\:ss");
            ValRawXpGain.Text = FormatValue(snapshot.RawXpGain);
            ValXpGain.Text = FormatValue(snapshot.XpGain);
            ValRawXpPerHour.Text = FormatValue(snapshot.RawXpPerHour);
            ValXpPerHour.Text = FormatValue(snapshot.XpPerHour);
            ValLoot.Text = FormatValue(snapshot.Loot);
            ValSupplies.Text = FormatValue(snapshot.Supplies);
            ValBalance.Text = FormatValue(snapshot.Balance);
            ValDamage.Text = FormatValue(snapshot.Damage);
            ValDamagePerHour.Text = FormatValue(snapshot.DamagePerHour);
            ValHealing.Text = FormatValue(snapshot.Healing);
            ValHealingPerHour.Text = FormatValue(snapshot.HealingPerHour);
            ValMonsters.Text = snapshot.KilledMonsters.Count > 0
                ? string.Join(", ", snapshot.KilledMonsters.Select(m => $"{m.Count}x {m.Name}"))
                : "\u2014";

            ColorValue(ValRawXpPerHour, snapshot.RawXpPerHour);
            ColorValue(ValXpPerHour, snapshot.XpPerHour);
            ColorValue(ValDamagePerHour, snapshot.DamagePerHour);
            ColorValue(ValHealingPerHour, snapshot.HealingPerHour);
        }
        else
        {
            ValSessionTime.Text = "\u2014";
            ValRawXpGain.Text = "\u2014";
            ValXpGain.Text = "\u2014";
            ValRawXpPerHour.Text = "\u2014";
            ValXpPerHour.Text = "\u2014";
            ValLoot.Text = "\u2014";
            ValSupplies.Text = "\u2014";
            ValBalance.Text = "\u2014";
            ValDamage.Text = "\u2014";
            ValDamagePerHour.Text = "\u2014";
            ValHealing.Text = "\u2014";
            ValHealingPerHour.Text = "\u2014";
            ValMonsters.Text = "\u2014";
        }

        // Preprocessed image (throttled to every 10 seconds)
        if (preprocessedPng != null && DateTime.UtcNow - _lastImageUpdate >= ImageRefreshInterval)
        {
            _lastImageUpdate = DateTime.UtcNow;
            UpdatePreprocessedImage(preprocessedPng);
        }

        // Spatial map
        RenderSpatialMap(words, valueColumnX);

        // Raw OCR lines with word positions
        var sb = new StringBuilder();
        sb.AppendLine("--- Reconstructed Lines ---");
        foreach (var line in ocrLines)
            sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("--- Raw Words (text @ x,y w\u00d7h) ---");
        foreach (var w in words)
            sb.AppendLine($"\"{w.Text}\" @ {w.X:F0},{w.Y:F0} {w.Width:F0}\u00d7{w.Height:F0}");
        RawOcrText.Text = sb.ToString();
    }

    public void UpdateSkillsData(int? stamina, IReadOnlyList<OcrWordInfo> words,
        byte[]? preprocessedPng = null, string? regionDebug = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateSkillsData(stamina, words, preprocessedPng, regionDebug));
            return;
        }

        // Status
        string status;
        if (words.Count > 0)
        {
            status = stamina.HasValue
                ? $"{words.Count} words → stamina={stamina} min"
                : $"{words.Count} words → no stamina parsed";
            if (regionDebug != null)
                status += $"   [{regionDebug}]";
        }
        else
        {
            status = "Skills panel not found";
            if (regionDebug != null)
                status += $"   [{regionDebug}]";
        }
        SkillsStatusText.Text = status;

        // Parsed values
        if (stamina.HasValue)
        {
            int hours = stamina.Value / 60;
            int mins = stamina.Value % 60;
            SkillsValStamina.Text = $"{hours}:{mins:D2}h ({stamina} min)";
            SkillsValStamina.Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
        }
        else
        {
            SkillsValStamina.Text = "—";
            SkillsValStamina.Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71));
        }

        // Preprocessed image (throttled)
        if (preprocessedPng != null && DateTime.UtcNow - _lastSkillsImageUpdate >= ImageRefreshInterval)
        {
            _lastSkillsImageUpdate = DateTime.UtcNow;
            UpdateSkillsPreprocessedImage(preprocessedPng);
        }

        // Spatial map
        RenderSkillsSpatialMap(words);

        // Raw OCR lines
        var sb = new StringBuilder();
        sb.AppendLine("--- Raw Words (text @ x,y w×h) ---");
        foreach (var w in words)
            sb.AppendLine($"\"{w.Text}\" @ {w.X:F0},{w.Y:F0} {w.Width:F0}×{w.Height:F0}");
        SkillsRawOcrText.Text = sb.ToString();
    }

    private void UpdateSkillsPreprocessedImage(byte[] pngBytes)
    {
        try
        {
            var bitmapImage = new BitmapImage();
            using (var ms = new MemoryStream(pngBytes))
            {
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
            }
            SkillsPreprocessedImage.Source = bitmapImage;
            SkillsPreprocessedTitle.Text = $"Preprocessed Image ({bitmapImage.PixelWidth}×{bitmapImage.PixelHeight}px)";
        }
        catch
        {
            SkillsPreprocessedTitle.Text = "Preprocessed Image (error loading)";
        }
    }

    private void RenderSkillsSpatialMap(IReadOnlyList<OcrWordInfo> words)
    {
        SkillsSpatialCanvas.Children.Clear();

        if (words.Count == 0)
        {
            SkillsSpatialTitle.Text = "Spatial Map (no words)";
            return;
        }

        // Find bounds of all words to compute scale
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var w in words)
        {
            if (w.X < minX) minX = w.X;
            if (w.Y < minY) minY = w.Y;
            if (w.Right > maxX) maxX = w.Right;
            if (w.Y + w.Height > maxY) maxY = w.Y + w.Height;
        }

        double dataWidth = maxX - minX;
        double dataHeight = maxY - minY;
        if (dataWidth <= 0 || dataHeight <= 0) return;

        double canvasW = SkillsSpatialCanvas.ActualWidth > 0 ? SkillsSpatialCanvas.ActualWidth : 480;
        double canvasH = SkillsSpatialCanvas.ActualHeight > 0 ? SkillsSpatialCanvas.ActualHeight : 210;
        double pad = 4;
        double scale = Math.Min((canvasW - pad * 2) / dataWidth, (canvasH - pad * 2) / dataHeight);

        SkillsSpatialTitle.Text = $"Spatial Map ({dataWidth:F0}×{dataHeight:F0}px crop)";

        // Draw word bounding boxes
        foreach (var w in words)
        {
            double x = pad + (w.X - minX) * scale;
            double y = pad + (w.Y - minY) * scale;
            double wScaled = w.Width * scale;
            double hScaled = w.Height * scale;

            bool isStamina = w.Text.Contains("Stamina", StringComparison.OrdinalIgnoreCase);
            var color = isStamina
                ? Color.FromRgb(0xfb, 0xbf, 0x24)  // gold for stamina
                : Color.FromRgb(0x60, 0xa5, 0xfa);  // blue for other labels

            var rect = new Rectangle
            {
                Width = Math.Max(wScaled, 2),
                Height = Math.Max(hScaled, 2),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B))
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            SkillsSpatialCanvas.Children.Add(rect);

            if (wScaled > 12)
            {
                var label = new TextBlock
                {
                    Text = w.Text,
                    FontSize = Math.Min(10, hScaled * 0.8),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(color),
                    MaxWidth = wScaled
                };
                Canvas.SetLeft(label, x + 1);
                Canvas.SetTop(label, y + 1);
                SkillsSpatialCanvas.Children.Add(label);
            }
        }
    }

    private void UpdatePreprocessedImage(byte[] pngBytes)
    {
        try
        {
            var bitmapImage = new BitmapImage();
            using (var ms = new MemoryStream(pngBytes))
            {
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
            }
            PreprocessedImage.Source = bitmapImage;
            PreprocessedTitle.Text = $"Preprocessed Image ({bitmapImage.PixelWidth}\u00d7{bitmapImage.PixelHeight}px)";
        }
        catch
        {
            PreprocessedTitle.Text = "Preprocessed Image (error loading)";
        }
    }

    private void RenderSpatialMap(IReadOnlyList<OcrWordInfo> words, double? valueColumnX)
    {
        SpatialCanvas.Children.Clear();

        if (words.Count == 0)
        {
            SpatialTitle.Text = "Spatial Map (no words)";
            return;
        }

        // Find bounds of all words to compute scale
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var w in words)
        {
            if (w.X < minX) minX = w.X;
            if (w.Y < minY) minY = w.Y;
            if (w.Right > maxX) maxX = w.Right;
            if (w.Y + w.Height > maxY) maxY = w.Y + w.Height;
        }

        double dataWidth = maxX - minX;
        double dataHeight = maxY - minY;
        if (dataWidth <= 0 || dataHeight <= 0) return;

        double canvasW = SpatialCanvas.ActualWidth > 0 ? SpatialCanvas.ActualWidth : 480;
        double canvasH = SpatialCanvas.ActualHeight > 0 ? SpatialCanvas.ActualHeight : 210;
        double pad = 4;
        double scale = Math.Min((canvasW - pad * 2) / dataWidth, (canvasH - pad * 2) / dataHeight);

        SpatialTitle.Text = $"Spatial Map ({dataWidth:F0}\u00d7{dataHeight:F0}px crop)";

        // Draw value column boundary line
        if (valueColumnX.HasValue)
        {
            double lineX = pad + (valueColumnX.Value - minX) * scale;
            var colLine = new Line
            {
                X1 = lineX, Y1 = 0, X2 = lineX, Y2 = canvasH,
                Stroke = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)), // gold
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.6
            };
            SpatialCanvas.Children.Add(colLine);

            // Also draw the minValueX threshold (0.5 * valueColumnX)
            double threshX = pad + (valueColumnX.Value * 0.5 - minX) * scale;
            if (threshX > pad)
            {
                var threshLine = new Line
                {
                    X1 = threshX, Y1 = 0, X2 = threshX, Y2 = canvasH,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)), // red
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 4 },
                    Opacity = 0.4
                };
                SpatialCanvas.Children.Add(threshLine);
            }
        }

        // Draw word bounding boxes
        foreach (var w in words)
        {
            double x = pad + (w.X - minX) * scale;
            double y = pad + (w.Y - minY) * scale;
            double width = w.Width * scale;
            double height = w.Height * scale;

            bool isNumeric = _parser.IsNumericWord(w.Text);
            var color = isNumeric
                ? Color.FromRgb(0x4a, 0xde, 0x80)  // green for values
                : Color.FromRgb(0x60, 0xa5, 0xfa);  // blue for labels

            var rect = new Rectangle
            {
                Width = Math.Max(width, 2),
                Height = Math.Max(height, 2),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B))
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            SpatialCanvas.Children.Add(rect);

            // Word text label (only if box is wide enough)
            if (width > 12)
            {
                var label = new TextBlock
                {
                    Text = w.Text,
                    FontSize = Math.Min(10, height * 0.8),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(color),
                    MaxWidth = width
                };
                Canvas.SetLeft(label, x + 1);
                Canvas.SetTop(label, y + 1);
                SpatialCanvas.Children.Add(label);
            }
        }
    }

    private static string FormatValue(long? value)
    {
        return value.HasValue ? value.Value.ToString("N0") : "\u2014";
    }

    private static void ColorValue(TextBlock tb, long? value)
    {
        tb.Foreground = value.HasValue
            ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)) // green
            : new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)); // red
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close so it can be re-shown from tray
        e.Cancel = true;
        SaveWindowBounds();
        Hide();
    }

    private void SaveWindowBounds()
    {
        try
        {
            var bounds = new { Left, Top, Width, Height, State = (int)WindowState };
            var json = JsonSerializer.Serialize(bounds);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BoundsFilePath)!);
            File.WriteAllText(BoundsFilePath, json);
        }
        catch { /* best effort */ }
    }

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(BoundsFilePath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(BoundsFilePath));
            var root = doc.RootElement;

            var left = root.GetProperty("Left").GetDouble();
            var top = root.GetProperty("Top").GetDouble();
            var width = root.GetProperty("Width").GetDouble();
            var height = root.GetProperty("Height").GetDouble();

            // Restore window state if saved
            bool shouldMaximize = false;
            if (root.TryGetProperty("State", out var stateEl))
                shouldMaximize = (WindowState)stateEl.GetInt32() == WindowState.Maximized;

            // Basic sanity: position isn't wildly offscreen (allow negative for multi-monitor)
            if (width > 50 && height > 50 && left > -5000 && top > -5000 && left < 10000 && top < 10000)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            // Maximize after position is set — defer to Loaded so WPF knows which
            // monitor the window is on before maximizing
            if (shouldMaximize)
            {
                Loaded += (_, _) => WindowState = WindowState.Maximized;
            }
        }
        catch { /* best effort — use defaults */ }
    }
}
