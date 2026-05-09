using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Processes raw session data: filters outliers, computes averages, downsamples, builds monster list.
/// Implemented by the Processing DLL.
/// </summary>
public interface ISessionProcessor
{
    ProcessedSession Process(HuntSession session, List<StoredSnapshot> snapshots);
}
