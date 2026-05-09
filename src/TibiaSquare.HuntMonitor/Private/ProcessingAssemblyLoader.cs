using System.IO;
using System.Reflection;
using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Storage;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Loads TibiaSquare.HuntMonitor.Processing.dll at runtime via reflection.
/// The processing DLL is distributed separately; the public project never has a
/// compile-time reference to it.
///
/// If the DLL is missing (e.g. fresh clone without the binary), all factory
/// methods return null and the app degrades gracefully (no OCR, no session tracking).
/// </summary>
public static class ProcessingAssemblyLoader
{
    private static Assembly? _assembly;
    private static bool _attempted;
    private static readonly object _lock = new();

    /// <summary>
    /// Attempts to load the Processing assembly. Returns null if not found.
    /// </summary>
    private static Assembly? GetAssembly()
    {
        if (_attempted) return _assembly;
        lock (_lock)
        {
            if (_attempted) return _assembly;
            _attempted = true;

            var baseDir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(baseDir, "lib", "TibiaSquare.HuntMonitor.Processing.dll");

            if (!File.Exists(dllPath))
            {
                // Try next to the executable (flat layout)
                dllPath = Path.Combine(baseDir, "TibiaSquare.HuntMonitor.Processing.dll");
            }

            if (!File.Exists(dllPath))
                return null;

            try
            {
                _assembly = Assembly.LoadFrom(dllPath);
            }
            catch
            {
                _assembly = null;
            }

            return _assembly;
        }
    }

    /// <summary>
    /// Creates an IImagePreprocessor from the Processing DLL.
    /// Used by TesseractOcrService and panel locators.
    /// </summary>
    public static IImagePreprocessor? CreateImagePreprocessor()
    {
        return CreateInstance<IImagePreprocessor>(
            "TibiaSquare.HuntMonitor.Processing.ImagePreprocessor");
    }

    /// <summary>
    /// Creates an IHuntAnalyserParser from the Processing DLL.
    /// Used by PeriodicCaptureLoop, SkillsParser, DebugOverlayWindow.
    /// </summary>
    public static IHuntAnalyserParser? CreateHuntAnalyserParser()
    {
        return CreateInstance<IHuntAnalyserParser>(
            "TibiaSquare.HuntMonitor.Processing.HuntAnalyserParserInstance");
    }

    /// <summary>
    /// Creates an AnalyserRegionLocator (IPanelLocator) from the Processing DLL.
    /// </summary>
    public static IPanelLocator? CreateAnalyserRegionLocator(ILogger logger, IImagePreprocessor preprocessor)
    {
        return CreateInstanceWithArgs<IPanelLocator>(
            "TibiaSquare.HuntMonitor.Processing.AnalyserRegionLocator",
            [logger, preprocessor]);
    }

    /// <summary>
    /// Creates a SkillsPanelLocator (IPanelLocator) from the Processing DLL.
    /// </summary>
    public static IPanelLocator? CreateSkillsPanelLocator(ILogger logger, IImagePreprocessor preprocessor)
    {
        return CreateInstanceWithArgs<IPanelLocator>(
            "TibiaSquare.HuntMonitor.Processing.SkillsPanelLocator",
            [logger, preprocessor]);
    }

    /// <summary>
    /// Creates an ISessionProcessor from the Processing DLL.
    /// Used by SyncService to process session data before upload.
    /// </summary>
    public static ISessionProcessor? CreateSessionProcessor()
    {
        return CreateInstance<ISessionProcessor>(
            "TibiaSquare.HuntMonitor.Processing.SessionProcessor");
    }

    /// <summary>
    /// Creates an ISessionLifecycle (SessionManager) from the Processing DLL.
    /// Used by AppLifecycleManager for session state management.
    /// </summary>
    public static ISessionLifecycle? CreateSessionManager(SqliteSessionStore store, ILogger logger)
    {
        return CreateInstanceWithArgs<ISessionLifecycle>(
            "TibiaSquare.HuntMonitor.Processing.SessionManager",
            [store, logger]);
    }

    private static T? CreateInstance<T>(string typeName) where T : class
    {
        var asm = GetAssembly();
        if (asm == null) return null;

        var type = asm.GetType(typeName);
        if (type == null) return null;

        try
        {
            return Activator.CreateInstance(type) as T;
        }
        catch
        {
            return null;
        }
    }

    private static T? CreateInstanceWithArgs<T>(string typeName, object[] args) where T : class
    {
        var asm = GetAssembly();
        if (asm == null) return null;

        var type = asm.GetType(typeName);
        if (type == null) return null;

        try
        {
            return Activator.CreateInstance(type, args) as T;
        }
        catch
        {
            return null;
        }
    }
}
