using System.Runtime.InteropServices;

namespace TibiaSquare.HuntMonitor.Infrastructure;

/// <summary>
/// Controls Windows Efficiency Mode (power throttling) for the current process.
/// When enabled, Windows reduces CPU frequency and priority, showing the green leaf icon in Task Manager.
/// </summary>
public static class EfficiencyMode
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static void Enable()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            };

            SetProcessInformation(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // Silently fail on older Windows versions
        }
    }

    public static void Disable()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0,
            };

            SetProcessInformation(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // Silently fail on older Windows versions
        }
    }
}
