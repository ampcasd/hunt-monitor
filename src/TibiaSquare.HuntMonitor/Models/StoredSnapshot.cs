namespace TibiaSquare.HuntMonitor.Models;

public sealed class StoredSnapshot
{
    public string? OcrProvenance { get; init; }
    public string RawXpRateQuality { get; init; } = "unverified";
    public string XpRateQuality { get; init; } = "unverified";
    public long Id { get; init; }
    public required string SessionId { get; init; }
    public int SessionTimeSeconds { get; init; }
    public long? RawXpGain { get; init; }
    public long? XpGain { get; init; }
    public long? RawXpPerHour { get; init; }
    public long? XpPerHour { get; init; }
    public long? Loot { get; init; }
    public long? Supplies { get; init; }
    public long? Balance { get; init; }
    public long? Damage { get; init; }
    public long? DamagePerHour { get; init; }
    public long? Healing { get; init; }
    public long? HealingPerHour { get; init; }
    public int? Stamina { get; init; }
    public IReadOnlyList<KilledMonster> KilledMonsters { get; init; } = [];
}
