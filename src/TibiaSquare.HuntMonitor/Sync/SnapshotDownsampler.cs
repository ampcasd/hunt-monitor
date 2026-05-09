using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Sync;

public static class SnapshotDownsampler
{
    private const int IntervalSeconds = 60;

    /// <summary>
    /// Downsamples snapshots to ~1 per 60 seconds.
    /// Always includes first and last snapshot.
    /// For each minute mark, picks the snapshot closest to it.
    /// </summary>
    public static List<StoredSnapshot> Downsample(IReadOnlyList<StoredSnapshot> snapshots)
    {
        if (snapshots.Count <= 2)
            return snapshots.ToList();

        var result = new List<StoredSnapshot> { snapshots[0] };

        var maxTime = snapshots[^1].SessionTimeSeconds;
        var minuteMark = IntervalSeconds;

        while (minuteMark < maxTime)
        {
            // Find the snapshot closest to this minute mark
            StoredSnapshot? closest = null;
            var minDiff = int.MaxValue;

            foreach (var snap in snapshots)
            {
                var diff = Math.Abs(snap.SessionTimeSeconds - minuteMark);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = snap;
                }
            }

            if (closest != null && closest != result[^1])
            {
                result.Add(closest);
            }

            minuteMark += IntervalSeconds;
        }

        // Always include last
        if (result[^1] != snapshots[^1])
        {
            result.Add(snapshots[^1]);
        }

        return result;
    }
}
