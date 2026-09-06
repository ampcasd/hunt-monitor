using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private IImagePreprocessor? _preprocessor;
    private bool _syncingPreprocessingControls;
    private long _settingsRevision;
    private long _lastPreviewRevision = -1;
    private string? _lastPreviewFingerprint;
    private string _currentPresetName = "Default";
    private DateTime _lastImageUpdate = DateTime.MinValue;
    private DateTime _lastSkillsImageUpdate = DateTime.MinValue;
    private DateTime _lastXpAnalyserImageUpdate = DateTime.MinValue;
    private static readonly TimeSpan ImageRefreshInterval = TimeSpan.FromSeconds(3);

    private static readonly IReadOnlyList<PreprocessingPreset> BuiltInPreprocessingPresets =
    [
        new("Custom", null, true),
        new("Default", new ImagePreprocessingSettings(), true),
        new("Dot rescue", new ImagePreprocessingSettings
        {
            UpscaleFactor = 4,
            BinarizationThreshold = 155,
            DilationRadius = 0,
            DotEnhancementRadius = 1,
            MinBlobArea = 8
        }, true),
        new("Crisp + thin", new ImagePreprocessingSettings
        {
            UpscaleFactor = 4,
            BinarizationThreshold = 145,
            DilationRadius = 0,
            DotEnhancementRadius = 1,
            MinBlobArea = 10
        }, true),
        new("Soft edges", new ImagePreprocessingSettings
        {
            UpscaleFactor = 4,
            BinarizationThreshold = 165,
            SmoothingRadius = 1,
            DilationRadius = 0,
            DotEnhancementRadius = 1,
            MinBlobArea = 10
        }, true),
        new("Bold", new ImagePreprocessingSettings
        {
            UpscaleFactor = 4,
            BinarizationThreshold = 155,
            DilationRadius = 1,
            DotEnhancementRadius = 0,
            MinBlobArea = 10
        }, true),
        new("Raw diagnostic", new ImagePreprocessingSettings
        {
            DilationRadius = 0,
            MinBlobArea = 0,
            EnableColorEnhancement = false,
            EnableCoinBlanking = false,
            EnableBottomBorderCrop = false,
            EnableSmallBlobRemoval = false
        }, true)
    ];

    private readonly List<PreprocessingPreset> _preprocessingPresets = [.. BuiltInPreprocessingPresets];

    private static readonly string BoundsFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare", "debug-overlay-bounds.json");

    private static readonly string PresetsFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare", "ocr-preprocessing-presets.json");

    public DebugOverlayWindow(IHuntAnalyserParser parser)
    {
        _parser = parser;
        InitializeComponent();

        _preprocessingPresets.AddRange(LoadUserPreprocessingPresets());
        RefreshPresetItems(selectedIndex: 1);

        // Keep Hunt Analyser first without duplicating the large tab contents in XAML.
        MainTabs.Items.Remove(HuntAnalyserTab);
        MainTabs.Items.Insert(0, HuntAnalyserTab);
        HuntAnalyserTab.IsSelected = true;

        RestoreWindowBounds();
        LocationChanged += (_, _) => SaveWindowBounds();
        SizeChanged += (_, _) => SaveWindowBounds();
        StateChanged += (_, _) => SaveWindowBounds();
    }

    /// <summary>
    /// Connects the debug controls to the preprocessor shared by every OCR panel.
    /// Called again when capture resources are recreated.
    /// </summary>
    public void AttachPreprocessor(IImagePreprocessor preprocessor)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AttachPreprocessor(preprocessor));
            return;
        }

        _preprocessor = preprocessor;
        _settingsRevision = Math.Max(_settingsRevision, preprocessor.Settings.Revision);
        SyncPreprocessingControls(preprocessor.Settings);
        PreprocessingSettingsPanel.IsEnabled = true;
        PreprocessingStatusText.Text = "Live · changes apply to Hunt Analyser, Skills, and XP Analyser";
    }

    private void PreprocessingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPreprocessingControls || _preprocessor == null)
            return;

        _syncingPreprocessingControls = true;
        UpdatePreprocessingTextValues();
        _syncingPreprocessingControls = false;
        MarkCustomPreset();
        ApplyPreprocessingSettings();
    }

    private void PreprocessingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingPreprocessingControls)
        {
            MarkCustomPreset();
            ApplyPreprocessingSettings();
        }
    }

    private void PreprocessingPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = PreprocessingPresetCombo.SelectedIndex;
        if (_syncingPreprocessingControls || index < 0 || index >= _preprocessingPresets.Count)
            return;

        var preset = _preprocessingPresets[index];
        UpdatePresetEditor(preset);
        if (_preprocessor == null || preset.Settings == null)
        {
            _currentPresetName = "Custom";
            return;
        }

        _currentPresetName = preset.Name;
        SyncPreprocessingControls(preset.Settings);
        ApplyPreprocessingSettings();
    }

    private void PresetName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        SaveCurrentPreset();
        e.Handled = true;
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e) => SaveCurrentPreset();

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        int index = PreprocessingPresetCombo.SelectedIndex;
        if (index < 0 || index >= _preprocessingPresets.Count || _preprocessingPresets[index].IsBuiltIn)
            return;

        string deletedName = _preprocessingPresets[index].Name;
        var updated = _preprocessingPresets.Where((_, itemIndex) => itemIndex != index).ToList();
        if (!PersistUserPreprocessingPresets(updated))
        {
            PreprocessingStatusText.Text = $"Could not delete “{deletedName}” · preset file is unavailable";
            return;
        }

        _preprocessingPresets.Clear();
        _preprocessingPresets.AddRange(updated);
        _currentPresetName = "Custom";
        PresetNameText.Text = string.Empty;
        RefreshPresetItems(selectedIndex: 0);
        PreprocessingStatusText.Text = $"Deleted “{deletedName}” · current image settings were kept";
    }

    private void PreprocessingText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitPreprocessingText(sender as TextBox);
    }

    private void PreprocessingText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitPreprocessingText(sender as TextBox);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ResetPreprocessing_Click(object sender, RoutedEventArgs e)
    {
        var defaultPreset = _preprocessingPresets[1];
        _currentPresetName = defaultPreset.Name;
        SyncPreprocessingControls(defaultPreset.Settings!);
        ApplyPreprocessingSettings();
    }

    private void CommitPreprocessingText(TextBox? textBox)
    {
        if (_syncingPreprocessingControls || _preprocessor == null || textBox == null)
            return;

        Slider? slider = textBox switch
        {
            _ when textBox == UpscaleText => UpscaleSlider,
            _ when textBox == ThresholdText => ThresholdSlider,
            _ when textBox == SmoothingText => SmoothingSlider,
            _ when textBox == DilationText => DilationSlider,
            _ when textBox == DotEnhancementText => DotEnhancementSlider,
            _ when textBox == BlobAreaText => BlobAreaSlider,
            _ => null
        };

        if (slider == null)
            return;

        int value = int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : (int)Math.Round(slider.Value);
        value = Math.Clamp(value, (int)slider.Minimum, (int)slider.Maximum);

        _syncingPreprocessingControls = true;
        slider.Value = value;
        textBox.Text = value.ToString(CultureInfo.InvariantCulture);
        _syncingPreprocessingControls = false;
        MarkCustomPreset();
        ApplyPreprocessingSettings();
    }

    private void SyncPreprocessingControls(ImagePreprocessingSettings settings)
    {
        _syncingPreprocessingControls = true;
        UpscaleSlider.Value = Math.Clamp(settings.UpscaleFactor, 1, 5);
        ThresholdSlider.Value = Math.Clamp(settings.BinarizationThreshold, 0, 255);
        SmoothingSlider.Value = Math.Clamp(settings.SmoothingRadius, 0, 2);
        DilationSlider.Value = Math.Clamp(settings.DilationRadius, 0, 3);
        DotEnhancementSlider.Value = Math.Clamp(settings.DotEnhancementRadius, 0, 2);
        BlobAreaSlider.Value = Math.Clamp(settings.MinBlobArea, 0, 500);
        ColorEnhancementCheck.IsChecked = settings.EnableColorEnhancement;
        CoinBlankingCheck.IsChecked = settings.EnableCoinBlanking;
        BottomBorderCheck.IsChecked = settings.EnableBottomBorderCrop;
        SmallBlobsCheck.IsChecked = settings.EnableSmallBlobRemoval;
        UpdatePreprocessingTextValues();
        SelectMatchingPreset(settings);
        _syncingPreprocessingControls = false;
    }

    private void UpdatePreprocessingTextValues()
    {
        UpscaleText.Text = ((int)Math.Round(UpscaleSlider.Value)).ToString(CultureInfo.InvariantCulture);
        ThresholdText.Text = ((int)Math.Round(ThresholdSlider.Value)).ToString(CultureInfo.InvariantCulture);
        SmoothingText.Text = ((int)Math.Round(SmoothingSlider.Value)).ToString(CultureInfo.InvariantCulture);
        DilationText.Text = ((int)Math.Round(DilationSlider.Value)).ToString(CultureInfo.InvariantCulture);
        DotEnhancementText.Text = ((int)Math.Round(DotEnhancementSlider.Value)).ToString(CultureInfo.InvariantCulture);
        BlobAreaText.Text = ((int)Math.Round(BlobAreaSlider.Value)).ToString(CultureInfo.InvariantCulture);
    }

    private void MarkCustomPreset()
    {
        string? editableName = null;
        int selectedIndex = PreprocessingPresetCombo.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < _preprocessingPresets.Count &&
            !_preprocessingPresets[selectedIndex].IsBuiltIn)
        {
            editableName = _preprocessingPresets[selectedIndex].Name;
        }

        _syncingPreprocessingControls = true;
        PreprocessingPresetCombo.SelectedIndex = 0;
        _currentPresetName = "Custom";
        _syncingPreprocessingControls = false;
        DeletePresetButton.IsEnabled = false;
        if (editableName != null)
            PresetNameText.Text = editableName;
    }

    private void SelectMatchingPreset(ImagePreprocessingSettings settings)
    {
        int index = -1;
        for (int i = 1; i < _preprocessingPresets.Count; i++)
        {
            if (SettingsMatch(_preprocessingPresets[i].Settings!, settings))
            {
                index = i;
                break;
            }
        }

        PreprocessingPresetCombo.SelectedIndex = index >= 0 ? index : 0;
        _currentPresetName = index >= 0 ? _preprocessingPresets[index].Name : "Custom";
        UpdatePresetEditor(index >= 0 ? _preprocessingPresets[index] : _preprocessingPresets[0]);
    }

    private static bool SettingsMatch(ImagePreprocessingSettings left, ImagePreprocessingSettings right)
    {
        return left.UpscaleFactor == right.UpscaleFactor
            && left.BinarizationThreshold == right.BinarizationThreshold
            && left.SmoothingRadius == right.SmoothingRadius
            && left.DilationRadius == right.DilationRadius
            && left.DotEnhancementRadius == right.DotEnhancementRadius
            && left.MinBlobArea == right.MinBlobArea
            && left.EnableColorEnhancement == right.EnableColorEnhancement
            && left.EnableCoinBlanking == right.EnableCoinBlanking
            && left.EnableBottomBorderCrop == right.EnableBottomBorderCrop
            && left.EnableSmallBlobRemoval == right.EnableSmallBlobRemoval;
    }

    private void ApplyPreprocessingSettings()
    {
        if (_preprocessor == null)
            return;

        _preprocessor.Settings = ReadPreprocessingSettings(++_settingsRevision);

        // Allow the very next capture to replace the preview instead of waiting for
        // the normal image throttle after a tuning change.
        _lastImageUpdate = DateTime.MinValue;
        PreprocessingStatusText.Text = $"Queued #{_settingsRevision} · {_currentPresetName} · waiting for next OCR frame";
    }

    private ImagePreprocessingSettings ReadPreprocessingSettings(long revision = 0) => new()
    {
        Revision = revision,
        UpscaleFactor = (int)Math.Round(UpscaleSlider.Value),
        BinarizationThreshold = (int)Math.Round(ThresholdSlider.Value),
        SmoothingRadius = (int)Math.Round(SmoothingSlider.Value),
        DilationRadius = (int)Math.Round(DilationSlider.Value),
        DotEnhancementRadius = (int)Math.Round(DotEnhancementSlider.Value),
        MinBlobArea = (int)Math.Round(BlobAreaSlider.Value),
        EnableColorEnhancement = ColorEnhancementCheck.IsChecked == true,
        EnableCoinBlanking = CoinBlankingCheck.IsChecked == true,
        EnableBottomBorderCrop = BottomBorderCheck.IsChecked == true,
        EnableSmallBlobRemoval = SmallBlobsCheck.IsChecked == true
    };

    private void SaveCurrentPreset()
    {
        string name = PresetNameText.Text.Trim();
        if (name.Length == 0)
        {
            PreprocessingStatusText.Text = "Enter a preset name before saving";
            PresetNameText.Focus();
            return;
        }

        if (BuiltInPreprocessingPresets.Any(preset =>
            string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            PreprocessingStatusText.Text = $"“{name}” is built in · choose another name";
            return;
        }

        var savedPreset = new PreprocessingPreset(name, ReadPreprocessingSettings(), false);
        var updated = _preprocessingPresets.ToList();
        int index = updated.FindIndex(preset => !preset.IsBuiltIn &&
            string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            updated[index] = savedPreset;
        else
        {
            updated.Add(savedPreset);
            index = updated.Count - 1;
        }

        if (!PersistUserPreprocessingPresets(updated))
        {
            PreprocessingStatusText.Text = $"Could not save “{name}” · preset file is unavailable";
            return;
        }

        _preprocessingPresets.Clear();
        _preprocessingPresets.AddRange(updated);
        _currentPresetName = name;
        RefreshPresetItems(index);
        PreprocessingStatusText.Text = $"Saved “{name}” · ready to reuse after restart";
    }

    private void RefreshPresetItems(int selectedIndex)
    {
        _syncingPreprocessingControls = true;
        PreprocessingPresetCombo.ItemsSource = _preprocessingPresets.Select(preset => preset.Name).ToList();
        PreprocessingPresetCombo.SelectedIndex = Math.Clamp(selectedIndex, 0, _preprocessingPresets.Count - 1);
        _syncingPreprocessingControls = false;
        UpdatePresetEditor(_preprocessingPresets[PreprocessingPresetCombo.SelectedIndex]);
    }

    private void UpdatePresetEditor(PreprocessingPreset preset)
    {
        bool isSavedUserPreset = !preset.IsBuiltIn;
        DeletePresetButton.IsEnabled = isSavedUserPreset;
        PresetNameText.Text = isSavedUserPreset ? preset.Name : string.Empty;
    }

    private static IReadOnlyList<PreprocessingPreset> LoadUserPreprocessingPresets()
    {
        try
        {
            if (!File.Exists(PresetsFilePath))
                return [];

            var stored = JsonSerializer.Deserialize<List<StoredPreprocessingPreset>>(
                File.ReadAllText(PresetsFilePath)) ?? [];
            var loaded = new List<PreprocessingPreset>();
            foreach (var item in stored)
            {
                string name = item.Name?.Trim() ?? string.Empty;
                if (name.Length == 0 || name.Length > 40 || item.Settings == null ||
                    BuiltInPreprocessingPresets.Any(preset =>
                        string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var preset = new PreprocessingPreset(name, NormalizeSettings(item.Settings), false);
                int duplicateIndex = loaded.FindIndex(existing =>
                    string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
                if (duplicateIndex >= 0)
                    loaded[duplicateIndex] = preset;
                else
                    loaded.Add(preset);
            }

            return loaded;
        }
        catch
        {
            return [];
        }
    }

    private static bool PersistUserPreprocessingPresets(IReadOnlyList<PreprocessingPreset> presets)
    {
        try
        {
            var stored = presets
                .Where(preset => !preset.IsBuiltIn && preset.Settings != null)
                .Select(preset => new StoredPreprocessingPreset(preset.Name, preset.Settings!))
                .ToList();
            string json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PresetsFilePath)!);
            string temporaryPath = PresetsFilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, PresetsFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ImagePreprocessingSettings NormalizeSettings(ImagePreprocessingSettings settings) => new()
    {
        UpscaleFactor = Math.Clamp(settings.UpscaleFactor, 1, 5),
        BinarizationThreshold = Math.Clamp(settings.BinarizationThreshold, 0, 255),
        SmoothingRadius = Math.Clamp(settings.SmoothingRadius, 0, 2),
        DilationRadius = Math.Clamp(settings.DilationRadius, 0, 3),
        DotEnhancementRadius = Math.Clamp(settings.DotEnhancementRadius, 0, 2),
        MinBlobArea = Math.Clamp(settings.MinBlobArea, 0, 500),
        EnableColorEnhancement = settings.EnableColorEnhancement,
        EnableCoinBlanking = settings.EnableCoinBlanking,
        EnableBottomBorderCrop = settings.EnableBottomBorderCrop,
        EnableSmallBlobRemoval = settings.EnableSmallBlobRemoval
    };

    private sealed record PreprocessingPreset(
        string Name,
        ImagePreprocessingSettings? Settings,
        bool IsBuiltIn);

    private sealed record StoredPreprocessingPreset(
        string? Name,
        ImagePreprocessingSettings? Settings);

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

        // Preprocessed image (throttled during normal capture; settings changes bypass it)
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

    public void UpdateXpAnalyserData(XpAnalyserRates? rates, IReadOnlyList<OcrWordInfo> words,
        byte[]? preprocessedPng = null, string? regionDebug = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateXpAnalyserData(rates, words, preprocessedPng, regionDebug));
            return;
        }

        string status;
        if (words.Count == 0)
        {
            status = "XP Analyser panel not found — using Hunt Analyser rates";
        }
        else if (rates is { XpPerHour: not null, RawXpPerHour: not null })
        {
            status = $"{words.Count} words → both rolling rates override Hunt Analyser";
        }
        else if (rates is { HasAnyRate: true })
        {
            status = $"{words.Count} words → one rolling rate overrides Hunt Analyser";
        }
        else
        {
            status = $"{words.Count} words → no rolling rate parsed; using Hunt Analyser rates";
        }

        if (regionDebug != null)
            status += $"   [{regionDebug}]";
        XpStatusText.Text = status;

        SetRateValue(XpValXpPerHour, rates?.XpPerHour);
        SetRateValue(XpValRawXpPerHour, rates?.RawXpPerHour);

        if (preprocessedPng != null && DateTime.UtcNow - _lastXpAnalyserImageUpdate >= ImageRefreshInterval)
        {
            _lastXpAnalyserImageUpdate = DateTime.UtcNow;
            UpdateXpPreprocessedImage(preprocessedPng);
        }

        RenderXpSpatialMap(words);

        var sb = new StringBuilder();
        sb.AppendLine("--- Raw Words (text @ x,y w×h) ---");
        foreach (var w in words)
            sb.AppendLine($"\"{w.Text}\" @ {w.X:F0},{w.Y:F0} {w.Width:F0}×{w.Height:F0}");
        XpRawOcrText.Text = sb.ToString();
    }

    private static void SetRateValue(TextBlock target, long? value)
    {
        target.Text = FormatValue(value);
        target.Foreground = new SolidColorBrush(value.HasValue
            ? Color.FromRgb(0x4a, 0xde, 0x80)
            : Color.FromRgb(0xf8, 0x71, 0x71));
    }

    private void UpdateXpPreprocessedImage(byte[] pngBytes)
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

            XpPreprocessedImage.Source = bitmapImage;
            XpPreprocessedTitle.Text = $"Preprocessed Image ({bitmapImage.PixelWidth}×{bitmapImage.PixelHeight}px)";
        }
        catch
        {
            XpPreprocessedTitle.Text = "Preprocessed Image (error loading)";
        }
    }

    private void RenderXpSpatialMap(IReadOnlyList<OcrWordInfo> words)
    {
        XpSpatialCanvas.Children.Clear();
        if (words.Count == 0)
        {
            XpSpatialTitle.Text = "Spatial Map (no words)";
            return;
        }

        double minX = words.Min(word => word.X);
        double minY = words.Min(word => word.Y);
        double maxX = words.Max(word => word.Right);
        double maxY = words.Max(word => word.Y + word.Height);
        double dataWidth = maxX - minX;
        double dataHeight = maxY - minY;
        if (dataWidth <= 0 || dataHeight <= 0)
            return;

        double canvasW = XpSpatialCanvas.ActualWidth > 0 ? XpSpatialCanvas.ActualWidth : 480;
        double canvasH = XpSpatialCanvas.ActualHeight > 0 ? XpSpatialCanvas.ActualHeight : 210;
        const double pad = 4;
        double scale = Math.Min((canvasW - pad * 2) / dataWidth, (canvasH - pad * 2) / dataHeight);
        XpSpatialTitle.Text = $"Spatial Map ({dataWidth:F0}×{dataHeight:F0}px crop)";

        foreach (var word in words)
        {
            double x = pad + (word.X - minX) * scale;
            double y = pad + (word.Y - minY) * scale;
            double width = word.Width * scale;
            double height = word.Height * scale;
            bool isXpLabel = word.Text.Contains("xp", StringComparison.OrdinalIgnoreCase)
                || word.Text.Contains("raw", StringComparison.OrdinalIgnoreCase);
            var color = isXpLabel
                ? Color.FromRgb(0xfb, 0xbf, 0x24)
                : Color.FromRgb(0x60, 0xa5, 0xfa);

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
            XpSpatialCanvas.Children.Add(rect);

            if (width > 12)
            {
                var label = new TextBlock
                {
                    Text = word.Text,
                    FontSize = Math.Min(10, height * 0.8),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(color),
                    MaxWidth = width
                };
                Canvas.SetLeft(label, x + 1);
                Canvas.SetTop(label, y + 1);
                XpSpatialCanvas.Children.Add(label);
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

            var grayscale = new FormatConvertedBitmap(bitmapImage, PixelFormats.Gray8, null, 0);
            int stride = grayscale.PixelWidth;
            var pixels = new byte[stride * grayscale.PixelHeight];
            grayscale.CopyPixels(pixels, stride, 0);
            double inkPercent = pixels.Count(pixel => pixel < 128) * 100.0 / pixels.Length;

            string fingerprint = Convert.ToHexString(SHA256.HashData(pngBytes).AsSpan(0, 4));
            var applied = _preprocessor?.LastAppliedSettings;
            if (applied != null)
            {
                bool renderedNewSettings = applied.Revision != _lastPreviewRevision;
                bool samePixels = renderedNewSettings
                    && _lastPreviewFingerprint != null
                    && fingerprint == _lastPreviewFingerprint;
                string result = samePixels ? "same pixels" : renderedNewSettings ? "pixels changed" : "live";
                string presetName = GetPresetName(applied);

                PreprocessingStatusText.Text = applied.Revision < _settingsRevision
                    ? $"Rendered #{applied.Revision} · #{_settingsRevision} still queued · ink {inkPercent:F1}%"
                    : $"Rendered #{applied.Revision} · {presetName} · {result} · ink {inkPercent:F1}%";
                _lastPreviewRevision = applied.Revision;
            }
            else
            {
                PreprocessingStatusText.Text = $"Live · ink {inkPercent:F1}%";
            }

            _lastPreviewFingerprint = fingerprint;
        }
        catch
        {
            PreprocessedTitle.Text = "Preprocessed Image (error loading)";
        }
    }

    private string GetPresetName(ImagePreprocessingSettings settings)
    {
        for (int i = 1; i < _preprocessingPresets.Count; i++)
        {
            if (SettingsMatch(_preprocessingPresets[i].Settings!, settings))
                return _preprocessingPresets[i].Name;
        }

        return "Custom";
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
