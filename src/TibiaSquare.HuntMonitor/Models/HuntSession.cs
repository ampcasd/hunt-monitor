namespace TibiaSquare.HuntMonitor.Models;

/// <summary>
/// Cumulative analyser values captured at the moment a session ends.
/// Used to compute deltas for the next session when the analyser wasn't reset.
/// </summary>
public record SessionBaseline
{
    public long? XpGain { get; init; }
    public long? Loot { get; init; }
    public long? Supplies { get; init; }
    public long? Balance { get; init; }
    public long? Damage { get; init; }
    public long? Healing { get; init; }
    /// <summary>Monster name → cumulative kill count at session end.</summary>
    public Dictionary<string, int> KilledMonsters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public class HuntSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string CharacterName { get; init; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public int ActiveDurationSeconds { get; set; }
    public string? EndReason { get; set; }
    public bool IsSynced { get; set; }
    /// <summary>
    /// Cumulative analyser values from the previous session's last snapshot.
    /// Null for sessions that started with a fresh (zeroed) analyser.
    /// </summary>
    public SessionBaseline? Baseline { get; set; }
}
