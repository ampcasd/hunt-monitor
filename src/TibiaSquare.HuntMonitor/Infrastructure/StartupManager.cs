using System.IO;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public static class StartupManager
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TibiaSquareHuntMonitor";
    private const string MsixStartupTaskId = "TibiaSquareHuntMonitorStartup";

    public static async Task<bool> IsEnabledAsync()
    {
        if (PackageHelper.IsMsixPackaged)
            return await GetMsixStartupEnabledAsync();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (PackageHelper.IsMsixPackaged)
        {
            await SetMsixStartupEnabledAsync(enabled);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "TibiaSquare.HuntMonitor.exe");
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Best effort — registry write might fail in rare scenarios
        }
    }

    private static async Task<bool> GetMsixStartupEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(MsixStartupTaskId);
            return task.State == StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SetMsixStartupEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(MsixStartupTaskId);

            if (enabled)
                await task.RequestEnableAsync();
            else
                task.Disable();
        }
        catch
        {
            // Best effort — startup task API might fail
        }
    }
}
