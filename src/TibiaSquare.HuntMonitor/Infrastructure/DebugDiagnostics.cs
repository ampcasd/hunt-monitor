using System.IO;
using System.Text.Json;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Ocr;

namespace TibiaSquare.HuntMonitor.Infrastructure;

/// <summary>
/// Saves per-tick OCR diagnostics to disk for live debugging.
/// Writes a JSONL file with every tick's data; saves the preprocessed PNG
/// only on all-zeros ticks (max once per 10 seconds) to avoid disk spam.
/// </summary>
public sealed class DebugDiagnostics : IDisposable
{
    private readonly string _captureDir;
    private readonly StreamWriter _jsonl;
    private readonly object _lock = new();
    private DateTime _lastImageSave = DateTime.MinValue;
    private static readonly TimeSpan ImageSaveInterval = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public DebugDiagnostics(IReadOnlySet<DateOnly>? keepDates = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _captureDir = Path.Combine(appData, "TibiaSquare", "debug-captures");
        Directory.CreateDirectory(_captureDir);

        // Clean up old PNGs from previous sessions (keep last 100)
        CleanupOldPngs(keep: 100);

        // Purge old diagnostics files if hunt dates provided
        if (keepDates != null)
            PurgeOldDiagnostics(keepDates);

        // Date-based filename with append mode
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var jsonlPath = Path.Combine(_captureDir, $"diagnostics-{today}.jsonl");
        _jsonl = new StreamWriter(jsonlPath, append: true) { AutoFlush = true };
        _jsonl.WriteLine($"{{\"session_start\":\"{DateTime.UtcNow:O}\"}}");
    }

    public void RecordTick(
        int tickCount,
        IReadOnlyList<OcrWordInfo> words,
        double? valueColumnX,
        IReadOnlyList<string> lines,
        HuntSnapshot? snapshot,
        byte[]? preprocessedPng,
        SessionState sessionState,
        int consecutiveCacheMisses)
    {
        var utcNow = DateTime.UtcNow;
        bool allZeros = snapshot is { XpGain: null or 0, RawXpGain: null or 0, Loot: null or 0,
            Supplies: null or 0, Damage: null or 0, Healing: null or 0 }
            && snapshot.KilledMonsters.Count == 0;

        // Save image only on all-zeros ticks, throttled to once per 10s
        bool imageSaved = false;
        if (allZeros && preprocessedPng != null && utcNow - _lastImageSave >= ImageSaveInterval)
        {
            var imgPath = Path.Combine(_captureDir, $"fail-t{tickCount}-{utcNow:HHmmss}.png");
            try
            {
                File.WriteAllBytes(imgPath, preprocessedPng);
                _lastImageSave = utcNow;
                imageSaved = true;
            }
            catch { }
        }

        var wordSummaries = words.Select(w => new
        {
            t = w.Text,
            x = Math.Round(w.X, 1),
            y = Math.Round(w.Y, 1),
            w = Math.Round(w.Width, 1),
            h = Math.Round(w.Height, 1)
        }).ToList();

        var entry = new
        {
            tick = tickCount,
            ts = utcNow.ToString("HH:mm:ss.fff"),
            state = sessionState.ToString(),
            words = words.Count,
            vColX = valueColumnX.HasValue ? Math.Round(valueColumnX.Value, 1) : (double?)null,
            cacheMiss = consecutiveCacheMisses,
            allZeros,
            imgSaved = imageSaved,
            lines = lines.Take(20).ToList(), // cap to avoid huge entries
            snap = snapshot == null ? null : new
            {
                xp = snapshot.XpGain,
                rawXp = snapshot.RawXpGain,
                xph = snapshot.XpPerHour,
                loot = snapshot.Loot,
                sup = snapshot.Supplies,
                bal = snapshot.Balance,
                dmg = snapshot.Damage,
                dmgph = snapshot.DamagePerHour,
                heal = snapshot.Healing,
                healph = snapshot.HealingPerHour,
                mobs = snapshot.KilledMonsters.Select(m => $"{m.Count}x{m.Name}").ToList()
            }
        };

        var json = JsonSerializer.Serialize(entry, JsonOpts);
        lock (_lock)
        {
            try { _jsonl.WriteLine(json); } catch { }
        }
    }

    private void PurgeOldDiagnostics(IReadOnlySet<DateOnly> keepDates)
    {
        try
        {
            foreach (var file in Directory.GetFiles(_captureDir, "diagnostics-*.jsonl"))
            {
                var date = ExtractDateFromFilename(file);
                if (date.HasValue && !keepDates.Contains(date.Value))
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static DateOnly? ExtractDateFromFilename(string filePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            // "diagnostics-2026-04-25"
            var parts = name.Split('-');
            if (parts.Length >= 3)
            {
                var dateStr = string.Join("-", parts[^3..]);
                if (DateOnly.TryParse(dateStr, out var date))
                    return date;
            }
        }
        catch { }
        return null;
    }

    private void CleanupOldPngs(int keep)
    {
        try
        {
            var pngs = new DirectoryInfo(_captureDir)
                .GetFiles("fail-*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var f in pngs.Skip(keep))
                f.Delete();
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _jsonl.Dispose();
        }
    }
}
