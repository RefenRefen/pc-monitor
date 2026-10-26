using PcMonitor.Collector.Aggregation;
using PcMonitor.Core;

namespace PcMonitor.Collector.Storage;

internal sealed class AppMinuteRow
{
    public long AppId;
    public double CpuAvg, CpuMax, RamAvg, RamMax, CommitMax, GpuAvg, GpuMax, VramMax;
    public long NetRx, NetTx, DiskRead, DiskWrite;
}

internal sealed class SystemMinuteRow
{
    public double CpuAvg, CpuMax, RamUsedAvg, RamTotal, GpuAvg, GpuMax, VramAvg;
    public long NetRx, NetTx, DiskRead, DiskWrite;
}

internal sealed class MinuteBatch
{
    /// <summary>Start of the minute, unix seconds (multiple of 60).</summary>
    public long Ts;
    /// <summary>Seconds of data actually collected within the minute (&lt; 60 for partial minutes).</summary>
    public double Duration;
    public SystemMinuteRow System = new();
    public List<AppMinuteRow> Apps = new();
}

/// <summary>
/// Folds 1 s ticks into one row per app per minute. Only apps that were "significant" at least once in the
/// minute get a row, which keeps the database small without hiding anything that mattered.
/// </summary>
internal sealed class MinuteAccumulator
{
    private sealed class AppAcc
    {
        public double CpuSum, GpuSum, RamSum;
        public double CpuMax, GpuMax, RamMax, CommitMax, VramMax;
        public double NetRx, NetTx, DiskRead, DiskWrite;
        public int Present;
        public bool Significant;
    }

    private readonly Dictionary<long, AppAcc> _apps = new();
    private long _minute = -1;
    private double _duration;
    private int _ticks;
    private double _cpuSum, _gpuSum, _ramSum, _vramSum, _cpuMax, _gpuMax, _ramTotal;
    private double _netRx, _netTx, _diskR, _diskW;

    /// <summary>Adds a tick; returns the completed previous minute when the minute rolls over.</summary>
    public MinuteBatch? Add(TickResult tick)
    {
        long minute = tick.Ts - tick.Ts % 60;
        MinuteBatch? finished = null;
        if (_minute >= 0 && minute != _minute)
        {
            finished = Complete();
        }
        _minute = minute;

        double dt = tick.Seconds;
        _duration += dt;
        _ticks++;
        var s = tick.System;
        _cpuSum += s.CpuPct; _cpuMax = Math.Max(_cpuMax, s.CpuPct);
        _gpuSum += s.GpuPct; _gpuMax = Math.Max(_gpuMax, s.GpuPct);
        _ramSum += s.RamUsedBytes; _ramTotal = s.RamTotalBytes;
        _vramSum += s.VramUsedBytes;
        _netRx += s.NetRxBps * dt; _netTx += s.NetTxBps * dt;
        _diskR += s.DiskReadBps * dt; _diskW += s.DiskWriteBps * dt;

        foreach (var a in tick.Apps)
        {
            if (!_apps.TryGetValue(a.AppId, out var acc)) _apps[a.AppId] = acc = new AppAcc();
            acc.Present++;
            acc.CpuSum += a.Cpu; acc.CpuMax = Math.Max(acc.CpuMax, a.Cpu);
            acc.GpuSum += a.Gpu; acc.GpuMax = Math.Max(acc.GpuMax, a.Gpu);
            acc.RamSum += a.Private; acc.RamMax = Math.Max(acc.RamMax, a.Private);
            acc.CommitMax = Math.Max(acc.CommitMax, a.Commit);
            acc.VramMax = Math.Max(acc.VramMax, a.VramDedicated);
            acc.NetRx += a.NetRx * dt; acc.NetTx += a.NetTx * dt;
            acc.DiskRead += a.DiskRead * dt; acc.DiskWrite += a.DiskWrite * dt;
            if (a.IsSignificant) acc.Significant = true;
        }
        return finished;
    }

    /// <summary>Flushes the partially filled minute (service shutdown).</summary>
    public MinuteBatch? Flush() => _ticks == 0 ? null : Complete();

    private MinuteBatch Complete()
    {
        double n = Math.Max(1, _ticks);
        var batch = new MinuteBatch
        {
            Ts = _minute,
            Duration = _duration,
            System = new SystemMinuteRow
            {
                CpuAvg = _cpuSum / n, CpuMax = _cpuMax,
                RamUsedAvg = _ramSum / n, RamTotal = _ramTotal,
                GpuAvg = _gpuSum / n, GpuMax = _gpuMax, VramAvg = _vramSum / n,
                NetRx = (long)_netRx, NetTx = (long)_netTx, DiskRead = (long)_diskR, DiskWrite = (long)_diskW,
            },
        };

        foreach (var (id, a) in _apps)
        {
            if (!a.Significant) continue;
            batch.Apps.Add(new AppMinuteRow
            {
                AppId = id,
                // CPU/GPU: absent seconds count as zero. RAM: average while the app was alive.
                CpuAvg = a.CpuSum / n, CpuMax = a.CpuMax,
                GpuAvg = a.GpuSum / n, GpuMax = a.GpuMax,
                RamAvg = a.RamSum / Math.Max(1, a.Present), RamMax = a.RamMax,
                CommitMax = a.CommitMax, VramMax = a.VramMax,
                NetRx = (long)a.NetRx, NetTx = (long)a.NetTx,
                DiskRead = (long)a.DiskRead, DiskWrite = (long)a.DiskWrite,
            });
        }

        _apps.Clear();
        _duration = 0; _ticks = 0;
        _cpuSum = _gpuSum = _ramSum = _vramSum = _cpuMax = _gpuMax = 0;
        _netRx = _netTx = _diskR = _diskW = 0;
        return batch;
    }
}
