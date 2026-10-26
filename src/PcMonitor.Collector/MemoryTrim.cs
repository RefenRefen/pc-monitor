using System.Runtime;
using System.Runtime.InteropServices;

namespace PcMonitor.Collector;

internal static class MemoryTrim
{
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>Compacts the managed heap and releases resident pages that are not in active use (they fault back in on demand).</summary>
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
