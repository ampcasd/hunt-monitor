using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Result of processing a raw hunt session — computed stats ready for sync.
/// </summary>
public sealed record ProcessedSession
{
    public required List<StoredSnapshot> DownsampledSnapshots { get; init; }
    public long? PeakXpPerHour { get; init; }
    public long? AverageTopXpPerHour { get; init; }
    public long? PeakRawXpPerHour { get; init; }
    public long? AverageTopRawXpPerHour { get; init; }
    public long? TotalXpGain { get; init; }
    public long? TotalLoot { get; init; }
    public long? TotalSupplies { get; init; }
    public long? FinalBalance { get; init; }
    public required List<KilledMonster> Monsters { get; init; }
}
