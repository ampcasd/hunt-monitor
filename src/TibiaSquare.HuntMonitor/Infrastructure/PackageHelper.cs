using Windows.ApplicationModel;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public static class PackageHelper
{
    private static readonly bool _isMsixPackaged = DetectMsixPackaged();

    public static bool IsMsixPackaged => _isMsixPackaged;

    private static bool DetectMsixPackaged()
    {
        try
        {
            // Accessing Package.Current throws if not running in a packaged context
            _ = Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
