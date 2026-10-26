using PcMonitor.Collector.Native;

namespace PcMonitor.Collector.Sampling;

/// <summary>Machine-wide CPU (GetSystemTimes) and physical memory.</summary>
internal sealed class SystemSampler
{
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _primed;

    public double CpuPct { get; private set; }
    public long RamTotalBytes { get; private set; }
    public long RamUsedBytes { get; private set; }

    public void Sample()
    {
        if (NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_primed)
            {
                long dIdle = idle - _prevIdle;
                long dTotal = (kernel - _prevKernel) + (user - _prevUser); // kernel time includes idle time
                CpuPct = dTotal > 0 ? Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100) : 0;
            }
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            _primed = true;
        }

        var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref ms))
        {
            RamTotalBytes = (long)ms.ullTotalPhys;
            RamUsedBytes = (long)(ms.ullTotalPhys - ms.ullAvailPhys);
        }
    }
}
