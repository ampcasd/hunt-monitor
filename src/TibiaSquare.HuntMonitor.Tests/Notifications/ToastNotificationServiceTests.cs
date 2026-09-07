using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Notifications;
using TibiaSquare.HuntMonitor.Sync;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Notifications;

public sealed class ToastNotificationServiceTests
{
    [Theory]
    [InlineData(true, false, "Hunt Analyser not visible")]
    [InlineData(false, true, "XP Analyser not visible")]
    [InlineData(true, true, "Analysers not visible")]
    public void AnalyserVisibilityWarning_GroupsMissingWidgets(
        bool huntAnalyserMissing,
        bool xpAnalyserMissing,
        string expectedTitle)
    {
        var content = ToastNotificationService.BuildAnalysersNotFoundContent(
            huntAnalyserMissing,
            xpAnalyserMissing);

        Assert.NotNull(content);
        Assert.Equal(expectedTitle, content.Value.Title);
    }

    [Fact]
    public void AnalyserVisibilityWarning_IsSuppressedWhenBothWidgetsAreVisible()
    {
        Assert.Null(ToastNotificationService.BuildAnalysersNotFoundContent(false, false));
    }

    [Theory]
    [InlineData(true, false, "Hunt Analyser settings")]
    [InlineData(false, true, "XP Analyser settings")]
    [InlineData(true, true, "both Hunt Analyser and XP Analyser settings")]
    public void RawXpWarning_NamesEveryAffectedAnalyser(
        bool huntAnalyserMissing,
        bool xpAnalyserMissing,
        string expectedSource)
    {
        var content = ToastNotificationService.BuildRawXpNotTrackedContent(
            huntAnalyserMissing,
            xpAnalyserMissing);

        Assert.NotNull(content);
        Assert.Contains(expectedSource, content.Value.Message);
    }

    [Fact]
    public void RawXpWarning_IsSuppressedWhenBothSourcesAreTracked()
    {
        var content = ToastNotificationService.BuildRawXpNotTrackedContent(false, false);

        Assert.Null(content);
    }

    [Theory]
    [InlineData(SyncResult.Success, "Successfully synced to tibiasquare.com")]
    [InlineData(SyncResult.Failed, "failed")]
    [InlineData(SyncResult.NotAuthenticated, "sign in")]
    public void SessionEnded_IncludesSyncOutcomeInTheSameNotification(
        SyncResult syncResult,
        string expectedSyncText)
    {
        var session = new HuntSession
        {
            Id = "session-1",
            CharacterName = "Knight Sample",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            ActiveDurationSeconds = 1_800,
        };
        var snapshot = new HuntSnapshot
        {
            SessionTime = TimeSpan.FromMinutes(30),
            XpGain = 1_000_000,
            Balance = 250_000,
        };

        var content = ToastNotificationService.BuildSessionEndedContent(
            session,
            SessionEndReason.InactivityTimeout,
            snapshot,
            syncResult);

        Assert.Equal("Hunt Session Saved", content.Title);
        Assert.Equal(2, content.Messages.Length);
        Assert.Contains(expectedSyncText, content.Messages[1], StringComparison.OrdinalIgnoreCase);
    }
}
