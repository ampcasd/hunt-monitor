using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TibiaSquare.HuntMonitor.Capture;

public record TibiaWindowInfo(IntPtr Hwnd, string? CharacterName)
{
    /// <summary>Window class name (e.g. "OgreD3D11Wnd")</summary>
    public string? WindowClass { get; init; }

    /// <summary>Process executable name (e.g. "client.exe")</summary>
    public string? ExecutableName { get; init; }

    /// <summary>Monitor resolution width in pixels (game rendering width).</summary>
    public int ClientWidth { get; init; }

    /// <summary>Monitor resolution height in pixels (game rendering height).</summary>
    public int ClientHeight { get; init; }

    /// <summary>
    /// OBS Game Capture window match string in "Title:Class:Exe" format.
    /// </summary>
    public string ObsWindowString
    {
        get
        {
            var title = CharacterName != null ? $"Tibia - {CharacterName}" : "Tibia";
            return $"{title}:{WindowClass ?? ""}:{ExecutableName ?? ""}";
        }
    }
}

public static partial class TibiaWindowDetector
{
    private static readonly Regex TitleWithNamePattern = new(
        @"^Tibia - (.+)$",
        RegexOptions.Compiled);

    /// <summary>The real game client exe. The launcher uses "Tibia.exe" and should be ignored.</summary>
    private const string GameClientExe = "client.exe";

    public static List<TibiaWindowInfo> FindAll()
    {
        var results = new List<TibiaWindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var title = GetWindowTitle(hwnd);
            if (title == null)
                return true;

            if (title == "Tibia")
            {
                results.Add(BuildWindowInfo(hwnd, null));
            }
            else
            {
                var match = TitleWithNamePattern.Match(title);
                if (match.Success)
                    results.Add(BuildWindowInfo(hwnd, match.Groups[1].Value));
            }

            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// Finds the first real Tibia game client window (exe=client.exe).
    /// Ignores the launcher (exe=Tibia.exe) which appears briefly during startup.
    /// </summary>
    public static TibiaWindowInfo? FindFirst()
    {
        TibiaWindowInfo? result = null;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var title = GetWindowTitle(hwnd);
            if (title == null)
                return true;

            string? characterName = null;
            if (title == "Tibia")
            {
                // Could be launcher or login screen — check exe name below
            }
            else
            {
                var match = TitleWithNamePattern.Match(title);
                if (!match.Success)
                    return true;
                characterName = match.Groups[1].Value;
            }

            // Only accept the real game client, not the launcher
            var info = BuildWindowInfo(hwnd, characterName);
            if (!string.Equals(info.ExecutableName, GameClientExe, StringComparison.OrdinalIgnoreCase))
                return true;

            result = info;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Returns true if the given HWND still has a logged-in character in its title ("Tibia - Name").
    /// Cheap: single GetWindowText call, no window enumeration.
    /// </summary>
    public static bool IsCharacterWindow(IntPtr hwnd)
    {
        var title = GetWindowTitle(hwnd);
        return title != null && TitleWithNamePattern.IsMatch(title);
    }

    private static TibiaWindowInfo BuildWindowInfo(IntPtr hwnd, string? characterName)
    {
        var (monW, monH) = GetMonitorResolution(hwnd);
        return new TibiaWindowInfo(hwnd, characterName)
        {
            WindowClass = GetWindowClassName(hwnd),
            ExecutableName = GetProcessExeName(hwnd),
            ClientWidth = monW,
            ClientHeight = monH,
        };
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0)
            return null;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int length = GetClassName(hwnd, sb, sb.Capacity);
        return length > 0 ? sb.ToString() : null;
    }

    private static string? GetProcessExeName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }


    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    /// <summary>
    /// Returns the resolution of the monitor that the window is primarily on.
    /// This matches the game's rendering resolution (unlike GetClientRect which
    /// subtracts the title bar height in windowed mode).
    /// </summary>
    private static (int Width, int Height) GetMonitorResolution(IntPtr hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hmon, ref info))
        {
            var w = info.rcMonitor.Right - info.rcMonitor.Left;
            var h = info.rcMonitor.Bottom - info.rcMonitor.Top;
            return (w, h);
        }
        return (1920, 1080); // fallback
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
