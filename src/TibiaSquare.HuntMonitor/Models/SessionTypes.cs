namespace TibiaSquare.HuntMonitor.Models;

public enum SessionEndReason
{
    AnalyserReset,
    InactivityTimeout,
    MobSetChanged,
    Logout,
    AppShutdown,
    ManualEnd,
}

public enum SessionTransition
{
    None,
    Started,
    Paused,
    Resumed,
    Ended,
}

public record SessionTransitionResult(
    SessionTransition Transition,
    SessionEndReason? EndReason = null,
    DateTime? SplitAt = null,
    double? NoveltyScore = null,
    IReadOnlyList<string>? CoreMonsters = null,
    IReadOnlyList<string>? NovelMonsters = null,
    SessionBaseline? Baseline = null);
