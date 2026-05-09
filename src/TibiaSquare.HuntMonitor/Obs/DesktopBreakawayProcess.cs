using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Launches a child process outside the parent's MSIX package container by setting
/// <c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c> =
/// <c>PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE</c>. Without this,
/// a child spawned from a packaged app inherits package identity and its DLL loader
/// searches the package graph instead of the exe's own directory — obs64.exe then
/// fails to find its neighbors in bin/64bit/ and exits with STATUS_DLL_NOT_FOUND
/// (0xC0000135). UseShellExecute=true does not fix this; the breakaway attribute is
/// the Microsoft-documented mechanism.
/// </summary>
internal static class DesktopBreakawayProcess
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    // ProcThreadAttributeValue(ProcThreadAttributeDesktopAppPolicy=18, Thread=FALSE, Input=TRUE, Additive=FALSE)
    //   = 18 | PROC_THREAD_ATTRIBUTE_INPUT(0x20000) = 0x00020012
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = new(0x00020012);
    private const uint PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE = 0x00000001;

    /// <summary>
    /// Creates the child process and returns its PID. Handles returned by the OS
    /// are closed immediately — callers that need a <see cref="System.Diagnostics.Process"/>
    /// should reattach via <c>Process.GetProcessById(pid)</c>.
    /// </summary>
    public static int Start(string exePath, string arguments, string workingDirectory)
    {
        // CreateProcess writes into lpCommandLine, so give it a mutable buffer.
        // Quote the exe path so args are parsed correctly even if the path has spaces.
        var cmdLine = new StringBuilder();
        cmdLine.Append('"').Append(exePath).Append('"');
        if (!string.IsNullOrEmpty(arguments))
            cmdLine.Append(' ').Append(arguments);

        IntPtr attrList = IntPtr.Zero;
        IntPtr policyBuf = IntPtr.Zero;
        try
        {
            // Size query: first call returns required size via `size` with last-error
            // = ERROR_INSUFFICIENT_BUFFER; return value is FALSE.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            policyBuf = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(policyBuf, (int)PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE);

            if (!UpdateProcThreadAttribute(
                    attrList,
                    0,
                    PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY,
                    policyBuf,
                    (IntPtr)sizeof(uint),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
            }

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attrList,
            };

            var ok = CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: cmdLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref si,
                lpProcessInformation: out var pi);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed");

            var pid = (int)pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pid;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (policyBuf != IntPtr.Zero)
                Marshal.FreeHGlobal(policyBuf);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
