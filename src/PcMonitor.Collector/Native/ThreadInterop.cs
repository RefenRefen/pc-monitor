using System.Runtime.InteropServices;

namespace PcMonitor.Collector.Native;

internal static class ThreadInterop
{
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int tid);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Owning process of a live thread, or 0 when it already exited / cannot be opened.</summary>
    public static int GetOwnerPid(int tid)
    {
        IntPtr h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (h == IntPtr.Zero) return 0;
        try { return (int)GetProcessIdOfThread(h); }
        finally { CloseHandle(h); }
    }
}
