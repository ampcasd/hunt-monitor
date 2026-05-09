using System.IO;
using System.Text.Json;

using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Generates OBS Portable config files so OBS launches pre-configured
/// with WebSocket enabled, minimized to tray, and ready for Game Capture.
/// </summary>
public static class ObsConfigGenerator
{
    private const string ProfileName = "Hunt Monitor";
    private const string SceneCollectionName = "Hunt Monitor";
    public const string SceneName = "Hunt Monitor";
    public const string GameCaptureSourceName = "Tibia Game Capture";

    public static void EnsureConfig(string obsPortableDir, int websocketPort, string websocketPassword)
    {
        var configRoot = Path.Combine(obsPortableDir, "config", "obs-studio");
        Directory.CreateDirectory(configRoot);

        // These are always force-written to ensure correct startup behavior
        WriteGlobalConfig(configRoot);
        WriteUserConfig(configRoot);
        WriteWebSocketConfig(configRoot, websocketPort, websocketPassword);

        // Profile is only written on first run — preserves canvas size set by UpdateCanvasSize
        // (e.g. for ultrawide monitors). Subsequent launches read the existing profile.
        WriteProfileIfMissing(configRoot);
    }

    /// <summary>
    /// Always overwritten — controls profile/scene selection and first-run state.
    /// OBS reads this for program-level settings.
    /// </summary>
    private static void WriteGlobalConfig(string configRoot)
    {
        var path = Path.Combine(configRoot, "global.ini");

        var content = $"""
            [General]
            FirstRun=false
            LastVersion=536870916
            Pre31Migrated=true
            MaxLogs=10
            ProcessPriority=Normal
            EnableAutoUpdates=false
            BrowserHWAccel=true

            [BasicWindow]
            SysTrayEnabled=true
            SysTrayWhenStarted=true
            SysTrayMinimizeToTray=true

            [Basic]
            Profile={ProfileName}
            ProfileDir={ProfileName}
            SceneCollection={SceneCollectionName}
            SceneCollectionFile={SceneCollectionName}

            [Video]
            Renderer=Direct3D 11

            [Audio]
            DisableAudioDucking=true
            """;

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Always overwritten — controls UI behavior, tray minimize, and first-run wizard.
    /// Without this file, OBS shows the auto-configuration wizard and ignores --minimize-to-tray.
    /// </summary>
    private static void WriteUserConfig(string configRoot)
    {
        var path = Path.Combine(configRoot, "user.ini");

        var content = $"""
            [General]
            FirstRun=false
            LastVersion=30.0.0
            Pre19Defaults=true
            Pre21Defaults=true
            Pre23Defaults=true
            Pre24.1Defaults=true
            ConfirmOnExit=false
            HotkeyFocusType=NeverDisableHotkeys

            [BasicWindow]
            SysTrayEnabled=true
            SysTrayWhenStarted=true
            SysTrayMinimizeToTray=true
            PreviewEnabled=true
            AlwaysOnTop=false

            [Basic]
            Profile={ProfileName}
            ProfileDir={ProfileName}
            SceneCollection={SceneCollectionName}
            SceneCollectionFile={SceneCollectionName}.json
            """;

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Writes the OBS profile if it doesn't already exist.
    /// Preserves canvas dimensions set by <see cref="UpdateCanvasSize"/> for non-16:9 monitors.
    /// </summary>
    private static void WriteProfileIfMissing(string configRoot)
    {
        var profileDir = Path.Combine(configRoot, "basic", "profiles", ProfileName);
        var basicIni = Path.Combine(profileDir, "basic.ini");
        if (!File.Exists(basicIni))
            WriteProfile(configRoot);
    }

    /// <summary>
    /// Writes the OBS profile with the specified canvas dimensions.
    /// Ensures 1 FPS and disabled audio for minimal resource usage.
    /// </summary>
    private static void WriteProfile(string configRoot, int width = 1920, int height = 1080)
    {
        var profileDir = Path.Combine(configRoot, "basic", "profiles", ProfileName);
        Directory.CreateDirectory(profileDir);

        var basicIni = Path.Combine(profileDir, "basic.ini");

        var content = $"""
            [General]
            Name=Hunt Monitor

            [Video]
            BaseCX={width}
            BaseCY={height}
            OutputCX={width}
            OutputCY={height}
            FPSType=2
            FPSInt=1

            [Audio]
            SampleRate=44100
            ChannelSetup=Stereo
            DesktopAudioDevice1=disabled
            DesktopAudioDevice2=disabled
            AuxAudioDevice1=disabled
            AuxAudioDevice2=disabled
            AuxAudioDevice3=disabled
            """;

        File.WriteAllText(basicIni, content);
    }

    /// <summary>
    /// Updates the OBS profile canvas size to match the detected game resolution.
    /// OBS reads this on startup, so a restart is required for the change to take effect.
    /// Returns true if the dimensions were changed (OBS restart needed).
    /// </summary>
    public static bool UpdateCanvasSize(string obsPortableDir, int width, int height)
    {
        var profileDir = Path.Combine(obsPortableDir, "config", "obs-studio", "basic", "profiles", ProfileName);
        var basicIni = Path.Combine(profileDir, "basic.ini");

        if (!File.Exists(basicIni))
        {
            // Profile doesn't exist yet — write it fresh
            var configRoot = Path.Combine(obsPortableDir, "config", "obs-studio");
            WriteProfile(configRoot, width, height);
            return true;
        }

        var lines = File.ReadAllLines(basicIni);
        bool changed = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("BaseCX="))
            {
                var newVal = $"BaseCX={width}";
                if (lines[i] != newVal) { lines[i] = newVal; changed = true; }
            }
            else if (lines[i].StartsWith("BaseCY="))
            {
                var newVal = $"BaseCY={height}";
                if (lines[i] != newVal) { lines[i] = newVal; changed = true; }
            }
            else if (lines[i].StartsWith("OutputCX="))
            {
                var newVal = $"OutputCX={width}";
                if (lines[i] != newVal) { lines[i] = newVal; changed = true; }
            }
            else if (lines[i].StartsWith("OutputCY="))
            {
                var newVal = $"OutputCY={height}";
                if (lines[i] != newVal) { lines[i] = newVal; changed = true; }
            }
        }

        if (changed)
            File.WriteAllLines(basicIni, lines);

        return changed;
    }

    /// <summary>
    /// Reads the current canvas size from the OBS profile.
    /// Returns (0, 0) if the profile doesn't exist or can't be parsed.
    /// </summary>
    public static (int Width, int Height) ReadCanvasSize(string obsPortableDir)
    {
        var basicIni = Path.Combine(obsPortableDir, "config", "obs-studio", "basic", "profiles", ProfileName, "basic.ini");
        if (!File.Exists(basicIni))
            return (0, 0);

        int w = 0, h = 0;
        foreach (var line in File.ReadAllLines(basicIni))
        {
            if (line.StartsWith("BaseCX=") && int.TryParse(line.AsSpan(7), out var cx)) w = cx;
            else if (line.StartsWith("BaseCY=") && int.TryParse(line.AsSpan(7), out var cy)) h = cy;
        }
        return (w, h);
    }

    /// <summary>
    /// Always overwritten to ensure WebSocket is enabled with correct port/password.
    /// OBS 32+ reads from config.json (not settings.json).
    /// </summary>
    private static void WriteWebSocketConfig(string configRoot, int port, string password)
    {
        var wsDir = Path.Combine(configRoot, "plugin_config", "obs-websocket");
        Directory.CreateDirectory(wsDir);

        var settings = new
        {
            server_enabled = true,
            server_port = port,
            server_password = password,
            auth_required = true,
            server_bind = "127.0.0.1",
            alerts_enabled = false,
            first_load = false,
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        // Write to both config.json (OBS 32+) and settings.json (older OBS)
        File.WriteAllText(Path.Combine(wsDir, "config.json"), json);
        File.WriteAllText(Path.Combine(wsDir, "settings.json"), json);
    }
}
