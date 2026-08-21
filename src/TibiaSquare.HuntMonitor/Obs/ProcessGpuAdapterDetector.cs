using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Identifies the GPU adapter used by a process from Windows' GPU Engine
/// performance-counter instances. This reads OS telemetry only; it never opens
/// or reads the target process.
/// </summary>
public sealed class ProcessGpuAdapterDetector
{
    private static readonly Regex InstancePattern = new(
        @"^pid_(?<pid>\d+)_luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)_",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool TryGetAdapter(int processId, out GpuAdapterId adapter)
    {
        adapter = default;

        try
        {
            var matches = new PerformanceCounterCategory("GPU Engine")
                .GetInstanceNames()
                .Select(instance => TryParseInstance(instance, processId, out var parsed)
                    ? parsed
                    : (GpuAdapterId?)null)
                .Where(candidate => candidate.HasValue)
                .Select(candidate => candidate!.Value)
                .GroupBy(candidate => candidate)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.HighPart)
                .ThenBy(group => group.Key.LowPart)
                .FirstOrDefault();

            if (matches == null)
                return false;

            adapter = matches.Key;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseInstance(string instanceName, int processId, out GpuAdapterId adapter)
    {
        adapter = default;
        var match = InstancePattern.Match(instanceName);
        if (!match.Success ||
            !int.TryParse(match.Groups["pid"].Value, out var parsedProcessId) ||
            parsedProcessId != processId ||
            !uint.TryParse(match.Groups["high"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) ||
            !uint.TryParse(match.Groups["low"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
        {
            return false;
        }

        adapter = new GpuAdapterId(high, low);
        return true;
    }
}

public readonly record struct GpuAdapterId(uint HighPart, uint LowPart)
{
    public override string ToString() => $"0x{HighPart:x8}_0x{LowPart:x8}";
}

/// <summary>
/// Manages Windows' per-application GPU preference for the bundled OBS binary.
/// Preference 1 is minimum-power and 2 is high-performance. Since those labels
/// do not identify an exact adapter, callers verify the launched OBS adapter and
/// probe the other preference automatically when necessary.
/// </summary>
public static class ObsGpuPreferenceStore
{
    private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly Regex PreferencePattern = new(
        @"GpuPreference=(?<preference>[12]);?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? GetPreference(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var value = key?.GetValue(executablePath) as string;
            var match = value == null ? Match.Empty : PreferencePattern.Match(value);
            return match.Success && int.TryParse(match.Groups["preference"].Value, out var preference)
                ? preference
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetPreference(string executablePath, int? preference)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            var existing = key.GetValue(executablePath) as string ?? string.Empty;
            var withoutPreference = PreferencePattern.Replace(existing, string.Empty).Trim();

            if (preference is 1 or 2)
            {
                var prefix = withoutPreference.Length > 0 && !withoutPreference.EndsWith(';')
                    ? withoutPreference + ";"
                    : withoutPreference;
                var updated = $"{prefix}GpuPreference={preference};";
                key.SetValue(executablePath, updated, RegistryValueKind.String);
            }
            else if (withoutPreference.Length == 0)
            {
                key.DeleteValue(executablePath, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(executablePath, withoutPreference, RegistryValueKind.String);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<int?> BuildProbeOrder(int? currentPreference)
    {
        var result = new List<int?>();
        AddUnique(currentPreference);
        AddUnique(null);
        AddUnique(1);
        AddUnique(2);
        return result;

        void AddUnique(int? value)
        {
            if (!result.Any(existing => existing == value))
                result.Add(value);
        }
    }
}
