using Microsoft.Toolkit.Uwp.Notifications;
using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Notifications;

public sealed class ToastNotificationService : IDisposable
{
    private DateTime _lastAnalyserNotFound = DateTime.MinValue;
    private static readonly TimeSpan AnalyserNotFoundCooldown = TimeSpan.FromMinutes(5);

    public void NotifySessionStarted(HuntSession session)
    {
        Show("New Hunt Session",
            $"New hunt session for {session.CharacterName}");
    }

    public void NotifySessionEnded(HuntSession session, SessionEndReason reason, HuntSnapshot? lastSnapshot)
    {
        var duration = lastSnapshot != null
            ? FormatDuration(lastSnapshot.SessionTime)
            : FormatDuration(TimeSpan.FromSeconds(session.ActiveDurationSeconds));

        var xp = lastSnapshot?.XpGain;
        var xpText = xp.HasValue ? $" \u2022 {FormatNumber(xp.Value)} XP" : "";

        var balance = lastSnapshot?.Balance;
        var balanceText = balance.HasValue ? $" \u2022 {FormatNumber(balance.Value)} balance" : "";

        if (reason == SessionEndReason.MobSetChanged)
        {
            Show("New Mobs Detected",
                $"Starting next session for {session.CharacterName} ({duration}{xpText})");
        }
        else
        {
            Show("Hunt Session Saved",
                $"{session.CharacterName} \u2014 {duration}{xpText}{balanceText}");
        }
    }

    public void NotifySyncSucceeded()
    {
        Show("Synced", "Session uploaded to tibia-square.com \u2713");
    }

    public void NotifySyncFailed()
    {
        Show("Sync failed", "Session saved locally \u2014 will retry on next launch");
    }

    public void NotifyPendingSyncRecovered(int syncedCount)
    {
        Show("Sessions synced",
            $"{syncedCount} pending session{(syncedCount == 1 ? "" : "s")} uploaded to tibia-square.com");
    }

    public void NotifySessionPaused(string characterName)
    {
        Show("Hunt Paused",
            $"{characterName} - no kills for 2 min");
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

    public void NotifyRawExpNotTracked()
    {
        Show("Raw Exp not tracked",
            "You might want to turn it on in Hunt Analyser settings in Tibia");
    }

    public void ShowError(string title, string message)
    {
        Show(title, message);
    }

    private static void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
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
