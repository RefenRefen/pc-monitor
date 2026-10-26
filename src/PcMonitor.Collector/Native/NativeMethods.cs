using System.Runtime.InteropServices;
using System.Text;

namespace PcMonitor.Collector.Native;

internal static unsafe class NativeMethods
{
    public const int SystemProcessInformation = 5;
    public const uint StatusInfoLengthMismatch = 0xC0000004;
    public const uint StatusBufferTooSmall = 0xC0000023;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryInformationProcess(IntPtr process, int infoClass, void* buffer, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char* buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    public static extern uint GetActiveProcessorCount(ushort group);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>Full Win32 path of a process, or null if it cannot be opened.</summary>
    public static string? TryGetImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            const int cap = 1024;
            char* buf = stackalloc char[cap];
            uint size = cap;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Command line via NtQueryInformationProcess(ProcessCommandLineInformation).</summary>
    public static string? TryGetCommandLine(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            const int ProcessCommandLineInformation = 60;
            uint len = 2048;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte* buf = (byte*)NativeMemory.Alloc(len);
                try
                {
                    uint status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out uint need);
                    if (status == 0)
                    {
                        // UNICODE_STRING { ushort Length; ushort MaxLength; wchar* Buffer }
                        ushort bytes = *(ushort*)buf;
                        char* chars = *(char**)(buf + 8);
                        return chars == null ? null : new string(chars, 0, bytes / 2);
                    }
                    if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall) return null;
                    len = need + 256;
                }
                finally { NativeMemory.Free(buf); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }
}
