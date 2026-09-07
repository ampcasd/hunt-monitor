using TibiaSquare.HuntMonitor.Infrastructure;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Infrastructure;

public sealed class RawXpNotificationTrackerTests
{
    [Theory]
    [InlineData(0L, 0L, null, false)]
    [InlineData(0L, null, 0L, false)]
    [InlineData(100L, 0L, null, false)]
    [InlineData(100L, null, null, true)]
    [InlineData(null, null, null, false)]
    public void IsRawXpMissing_TreatsParsedZeroAsVisible(
        long? xpValue,
        long? rawXpValue,
        long? alternateRawXpValue,
        bool expected)
    {
        Assert.Equal(expected, RawXpNotificationTracker.IsRawXpMissing(
            xpValue,
            rawXpValue,
            alternateRawXpValue));
    }

    [Fact]
    public void Observe_ReportsBothAnalysersInOneResult()
    {
        var tracker = new RawXpNotificationTracker();

        MissingRawXpSources? result = null;
        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames; i++)
            result = tracker.Observe(huntAnalyserMissing: true, xpAnalyserMissing: true);

        Assert.Equal(new MissingRawXpSources(true, true), result);
        Assert.Null(tracker.Observe(huntAnalyserMissing: true, xpAnalyserMissing: true));
    }

    [Fact]
    public void Observe_ReportsOnlyTheConfirmedSource()
    {
        var tracker = new RawXpNotificationTracker();

        MissingRawXpSources? result = null;
        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames; i++)
            result = tracker.Observe(huntAnalyserMissing: false, xpAnalyserMissing: true);

        Assert.Equal(new MissingRawXpSources(false, true), result);
    }

    [Fact]
    public void Observe_CanReportTheOtherSourceLater()
    {
        var tracker = new RawXpNotificationTracker();

        MissingRawXpSources? huntResult = null;
        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames; i++)
            huntResult = tracker.Observe(huntAnalyserMissing: true, xpAnalyserMissing: false);

        MissingRawXpSources? xpResult = null;
        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames; i++)
            xpResult = tracker.Observe(huntAnalyserMissing: false, xpAnalyserMissing: true);

        Assert.Equal(new MissingRawXpSources(true, false), huntResult);
        Assert.Equal(new MissingRawXpSources(false, true), xpResult);
    }

    [Fact]
    public void Observe_RequiresConsecutiveMissingFrames()
    {
        var tracker = new RawXpNotificationTracker();

        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames - 1; i++)
            Assert.Null(tracker.Observe(huntAnalyserMissing: true, xpAnalyserMissing: false));

        Assert.Null(tracker.Observe(huntAnalyserMissing: false, xpAnalyserMissing: false));

        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames - 1; i++)
            Assert.Null(tracker.Observe(huntAnalyserMissing: true, xpAnalyserMissing: false));
    }

    [Fact]
    public void DiscardPendingObservations_BreaksMissingFrameStreak()
    {
        var tracker = new RawXpNotificationTracker();

        for (int i = 0; i < RawXpNotificationTracker.ConfirmationFrames - 1; i++)
            Assert.Null(tracker.Observe(huntAnalyserMissing: false, xpAnalyserMissing: true));

        tracker.DiscardPendingObservations();

        Assert.Null(tracker.Observe(huntAnalyserMissing: false, xpAnalyserMissing: true));
    }
}
