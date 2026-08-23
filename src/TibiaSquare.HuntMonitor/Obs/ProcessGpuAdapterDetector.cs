using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
/// Builds the explicit OBS D3D11 adapter indices to try. OBS reads AdapterIdx
/// from user.ini and passes it directly to its graphics device creation.
/// </summary>
public static class ObsAdapterProbe
{
    public static IReadOnlyList<uint> BuildProbeOrder(uint? currentAdapterIndex, uint maxAdapterCount)
    {
        if (maxAdapterCount == 0)
            return Array.Empty<uint>();

        var result = new List<uint>((int)maxAdapterCount);
        if (currentAdapterIndex.HasValue && currentAdapterIndex.Value < maxAdapterCount)
            result.Add(currentAdapterIndex.Value);

        for (uint index = 0; index < maxAdapterCount; index++)
        {
            if (!result.Contains(index))
                result.Add(index);
        }

        return result;
    }
}
