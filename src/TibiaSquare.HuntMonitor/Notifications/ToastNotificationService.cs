using Microsoft.Toolkit.Uwp.Notifications;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Sync;

namespace TibiaSquare.HuntMonitor.Notifications;

public sealed class ToastNotificationService : IDisposable
{
    private DateTime _lastAnalyserNotFound = DateTime.MinValue;
    private DateTime _lastXpAnalyserNotFound = DateTime.MinValue;
    private static readonly TimeSpan AnalyserNotFoundCooldown = TimeSpan.FromMinutes(5);

    public void NotifySessionStarted(HuntSession session)
    {
        Show("New Hunt Session",
            $"New hunt session for {session.CharacterName}");
    }

    public void NotifySessionEnded(
        HuntSession session,
        SessionEndReason reason,
        HuntSnapshot? lastSnapshot,
        SyncResult syncResult)
    {
        var content = BuildSessionEndedContent(session, reason, lastSnapshot, syncResult);
        Show(content.Title, content.Messages);
    }

    internal static (string Title, string[] Messages) BuildSessionEndedContent(
        HuntSession session,
        SessionEndReason reason,
        HuntSnapshot? lastSnapshot,
        SyncResult syncResult)
    {
        var duration = lastSnapshot != null
            ? FormatDuration(lastSnapshot.SessionTime)
            : FormatDuration(TimeSpan.FromSeconds(session.ActiveDurationSeconds));

        var xp = lastSnapshot?.XpGain;
        var xpText = xp.HasValue ? $" \u2022 {FormatNumber(xp.Value)} XP" : "";

        var balance = lastSnapshot?.Balance;
        var balanceText = balance.HasValue ? $" \u2022 {FormatNumber(balance.Value)} balance" : "";

        var syncText = syncResult switch
        {
            SyncResult.Success => "Successfully synced to tibiasquare.com",
            SyncResult.Failed => "Sync to tibiasquare.com failed — saved locally and will retry next launch",
            _ => "Saved locally — sign in to sync with tibiasquare.com",
        };

        if (reason == SessionEndReason.MobSetChanged)
        {
            return ("New Mobs Detected",
            [
                $"Starting next session for {session.CharacterName} ({duration}{xpText})",
                syncText,
            ]);
        }

        return ("Hunt Session Saved",
        [
                $"{session.CharacterName} \u2014 {duration}{xpText}{balanceText}",
                syncText,
        ]);
    }

    public void NotifyPendingSyncRecovered(int syncedCount)
    {
        Show("Sessions synced",
            $"{syncedCount} pending session{(syncedCount == 1 ? "" : "s")} uploaded to tibia-square.com");
    }

    public void NotifyTibiaDetected()
    {
        Show("Tibia Detected",
            "Launching Hunt Monitor");
    }

    public void NotifyTibiaClosed()
    {
        Show("Tibia Closed",
            "Going to sleep...");
    }

    public void NotifyAnalyserNotFound()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAnalyserNotFound < AnalyserNotFoundCooldown)
            return;

        _lastAnalyserNotFound = now;
        Show("Hunt Analyser not visible",
            "Is it open and not covered by a tibia popup?");
    }

    public void NotifyXpAnalyserNotFound()
    {
        var now = DateTime.UtcNow;
        if (now - _lastXpAnalyserNotFound < AnalyserNotFoundCooldown)
            return;

        _lastXpAnalyserNotFound = now;
        Show("XP Analyser not visible",
            "Open it in Tibia and keep it uncovered to use rolling XP rates");
    }

    public void NotifyRawExpNotTracked(bool huntAnalyserMissing, bool xpAnalyserMissing)
    {
        var content = BuildRawXpNotTrackedContent(huntAnalyserMissing, xpAnalyserMissing);
        if (content == null)
            return;

        Show(content.Value.Title, content.Value.Message);
    }

    internal static (string Title, string Message)? BuildRawXpNotTrackedContent(
        bool huntAnalyserMissing,
        bool xpAnalyserMissing)
    {
        if (!huntAnalyserMissing && !xpAnalyserMissing)
            return null;

        var settings = (huntAnalyserMissing, xpAnalyserMissing) switch
        {
            (true, true) => "both Hunt Analyser and XP Analyser settings",
            (true, false) => "Hunt Analyser settings",
            _ => "XP Analyser settings",
        };

        return ("Raw XP not tracked",
            $"Enable raw XP in {settings} in Tibia");
    }

    public void ShowError(string title, string message)
    {
        Show(title, message);
    }

    private static void Show(string title, params string[] messages)
    {
        try
        {
            var toast = new ToastContentBuilder().AddText(title);
            foreach (var message in messages)
                toast.AddText(message);
            toast.Show();
        }
        catch
        {
            // Toast failures should never crash the app
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{duration.Minutes}m {duration.Seconds}s";
    }

    private static string FormatNumber(long value)
    {
        return value switch
        {
            >= 1_000_000 or <= -1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 1_000 or <= -1_000 => $"{value / 1_000.0:F0}k",
            _ => value.ToString("N0"),
        };
    }

    public void Dispose()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch
        {
            // Ignore cleanup failures
        }
    }
}
