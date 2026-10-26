using System.Runtime;
using System.Runtime.InteropServices;

namespace PcMonitor.Widget.Native;

internal static class MemoryTrim
{
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>Compacts the managed heap and returns unused resident pages to the system (they fault back on demand).</summary>
    public static void Run()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch { /* purely an optimisation */ }
    }
}
