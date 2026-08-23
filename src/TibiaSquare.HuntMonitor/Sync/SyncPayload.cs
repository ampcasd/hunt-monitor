using System.Text.Json.Serialization;

namespace TibiaSquare.HuntMonitor.Sync;

public sealed class SyncPayload
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("characterName")]
    public required string CharacterName { get; init; }

    [JsonPropertyName("startedAt")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public string? EndedAt { get; init; }

    [JsonPropertyName("activeDurationSeconds")]
    public int? ActiveDurationSeconds { get; init; }

    [JsonPropertyName("endReason")]
    public string? EndReason { get; init; }

    [JsonPropertyName("rateCalculationVersion")]
    public int RateCalculationVersion { get; init; }

    [JsonPropertyName("peakXpPerHour")]
    public long? PeakXpPerHour { get; init; }

    [JsonPropertyName("averageTopXpPerHour")]
    public long? AverageTopXpPerHour { get; init; }

    [JsonPropertyName("peakRawXpPerHour")]
    public long? PeakRawXpPerHour { get; init; }

    [JsonPropertyName("averageTopRawXpPerHour")]
    public long? AverageTopRawXpPerHour { get; init; }

    [JsonPropertyName("totalXpGain")]
    public long? TotalXpGain { get; init; }

    [JsonPropertyName("totalLoot")]
    public long? TotalLoot { get; init; }

    [JsonPropertyName("totalSupplies")]
    public long? TotalSupplies { get; init; }

    [JsonPropertyName("finalBalance")]
    public long? FinalBalance { get; init; }

    [JsonPropertyName("killedMonsters")]
    public List<MonsterKill>? KilledMonsters { get; init; }

    [JsonPropertyName("snapshots")]
    public required List<SnapshotPayload> Snapshots { get; init; }
}

public sealed class MonsterKill
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed class SnapshotPayload
{
    [JsonPropertyName("sessionTimeSeconds")]
    public int SessionTimeSeconds { get; init; }

    [JsonPropertyName("rawXpPerHour")]
    public long? RawXpPerHour { get; init; }

    [JsonPropertyName("xpPerHour")]
    public long? XpPerHour { get; init; }

    [JsonPropertyName("rawXpGain")]
    public long? RawXpGain { get; init; }

    [JsonPropertyName("xpGain")]
    public long? XpGain { get; init; }

    [JsonPropertyName("loot")]
    public long? Loot { get; init; }

    [JsonPropertyName("supplies")]
    public long? Supplies { get; init; }

    [JsonPropertyName("balance")]
    public long? Balance { get; init; }

    [JsonPropertyName("damagePerHour")]
    public long? DamagePerHour { get; init; }

    [JsonPropertyName("healingPerHour")]
    public long? HealingPerHour { get; init; }

    [JsonPropertyName("stamina")]
    public int? Stamina { get; init; }
}
