using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Sync;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Sync;

public class SnapshotDownsamplerTests
{
    private static StoredSnapshot MakeSnapshot(int sessionTimeSeconds) => new()
    {
        Id = sessionTimeSeconds,
        SessionId = "test",
        SessionTimeSeconds = sessionTimeSeconds,
        XpPerHour = sessionTimeSeconds * 100,
    };

    [Fact]
    public void Downsample_ReturnsSame_WhenTwoOrFewer()
    {
        var snapshots = new List<StoredSnapshot>
        {
            MakeSnapshot(0),
            MakeSnapshot(30),
        };

        var result = SnapshotDownsampler.Downsample(snapshots);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Downsample_AlwaysIncludesFirstAndLast()
    {
        var snapshots = Enumerable.Range(0, 100)
            .Select(i => MakeSnapshot(i * 30)) // 30-second intervals for 50 minutes
            .ToList();

        var result = SnapshotDownsampler.Downsample(snapshots);

        Assert.Equal(snapshots[0].SessionTimeSeconds, result[0].SessionTimeSeconds);
        Assert.Equal(snapshots[^1].SessionTimeSeconds, result[^1].SessionTimeSeconds);
    }

    [Fact]
    public void Downsample_ReducesCount_ForLongSession()
    {
        // 2-hour hunt with snapshots every 30 seconds = 240 snapshots
        var snapshots = Enumerable.Range(0, 240)
            .Select(i => MakeSnapshot(i * 30))
            .ToList();

        var result = SnapshotDownsampler.Downsample(snapshots);

        // Should be roughly 120 (1 per minute) + first + last
        Assert.True(result.Count < snapshots.Count, "Should reduce count");
        Assert.True(result.Count >= 100 && result.Count <= 130,
            $"Expected ~120 snapshots, got {result.Count}");
    }

    [Fact]
    public void Downsample_PreservesOrder()
    {
        var snapshots = Enumerable.Range(0, 50)
            .Select(i => MakeSnapshot(i * 30))
            .ToList();

        var result = SnapshotDownsampler.Downsample(snapshots);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].SessionTimeSeconds > result[i - 1].SessionTimeSeconds,
                $"Snapshot at index {i} should be after index {i - 1}");
        }
    }

    [Fact]
    public void Downsample_HandlesExactMinuteMarks()
    {
        // Snapshots exactly on minute marks
        var snapshots = Enumerable.Range(0, 10)
            .Select(i => MakeSnapshot(i * 60))
            .ToList();

        var result = SnapshotDownsampler.Downsample(snapshots);
        Assert.Equal(snapshots.Count, result.Count);
    }
}
