using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Session lifecycle management — state transitions, session splitting, baseline carry-over.
/// Implemented by the Processing DLL.
/// </summary>
public interface ISessionLifecycle
{
    SessionState State { get; }
    HuntSession? CurrentSession { get; }

    event Action<HuntSession>? SessionStarted;
    event Action<HuntSession, SessionEndReason>? SessionEnded;
    event Action? SessionPaused;
    event Action? SessionResumed;

    void RecoverUnfinishedSessions();
    void OnSnapshot(HuntSnapshot snapshot, string characterName);
    void OnWindowLost();
    void OnShutdown();
    bool EndSessionManually();
}
