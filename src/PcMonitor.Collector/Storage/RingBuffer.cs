using PcMonitor.Collector.Aggregation;
using PcMonitor.Core;

namespace PcMonitor.Collector.Storage;

/// <summary>One second of history held in memory (never written to disk).</summary>
internal sealed record TickRecord(long Ts, double Seconds, SystemTotals System, AppTick[] Apps);

/// <summary>Fixed-size circular buffer: 600 entries = the last 10 minutes at 1 s resolution.</summary>
internal sealed class TickRing
{
    private readonly TickRecord?[] _items;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public TickRing(int capacity = Resolution.LiveWindowSeconds) => _items = new TickRecord?[capacity];

    public int Capacity => _items.Length;

    public int Count { get { lock (_gate) return _count; } }

    public void Append(TickRecord record)
    {
        lock (_gate)
        {
            _items[_next] = record;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    /// <summary>Copies records in chronological order whose timestamp lies in [from, to].</summary>
    public List<TickRecord> Snapshot(long fromUnix = long.MinValue, long toUnix = long.MaxValue)
    {
        var list = new List<TickRecord>(Math.Min(_items.Length, 700));
        lock (_gate)
        {
            int start = (_next - _count + _items.Length) % _items.Length;
            for (int i = 0; i < _count; i++)
            {
                var r = _items[(start + i) % _items.Length]!;
                if (r.Ts >= fromUnix && r.Ts <= toUnix) list.Add(r);
            }
        }
        return list;
    }

    public TickRecord? Latest()
    {
        lock (_gate) return _count == 0 ? null : _items[(_next - 1 + _items.Length) % _items.Length];
    }

    public static List<SeriesPoint> ToSystemSeries(IReadOnlyList<TickRecord> records)
    {
        var points = new List<SeriesPoint>(records.Count);
        foreach (var r in records)
        {
            var s = r.System;
            points.Add(new SeriesPoint
            {
                Ts = r.Ts,
                Cpu = s.CpuPct, CpuMax = s.CpuPct,
                Ram = s.RamUsedBytes,
                Gpu = s.GpuPct, GpuMax = s.GpuPct,
                Vram = s.VramUsedBytes,
                NetRx = s.NetRxBps, NetTx = s.NetTxBps,
                DiskRead = s.DiskReadBps, DiskWrite = s.DiskWriteBps,
            });
        }
        return points;
    }

    public static List<SeriesPoint> ToAppSeries(IReadOnlyList<TickRecord> records, long appId)
    {
        var points = new List<SeriesPoint>(records.Count);
        foreach (var r in records)
        {
            var p = new SeriesPoint { Ts = r.Ts };
            foreach (var a in r.Apps)
            {
                if (a.AppId != appId) continue;
                p.Cpu = p.CpuMax = a.Cpu;
                p.Ram = a.Private;
                p.Gpu = p.GpuMax = a.Gpu;
                p.Vram = a.VramDedicated;
                p.NetRx = a.NetRx; p.NetTx = a.NetTx;
                p.DiskRead = a.DiskRead; p.DiskWrite = a.DiskWrite;
                break;
            }
            points.Add(p);
        }
        return points;
    }

    /// <summary>Per-app aggregate over the ring (used for the "Live" range, no database involved).</summary>
    public static List<AppSummary> Summarize(IReadOnlyList<TickRecord> records, Func<long, (string Name, string? Path)> names)
    {
        var acc = new Dictionary<long, SummaryAcc>();
        double totalSeconds = 0;
        foreach (var r in records)
        {
            totalSeconds += r.Seconds;
            foreach (var a in r.Apps)
            {
                if (!acc.TryGetValue(a.AppId, out var s)) acc[a.AppId] = s = new SummaryAcc();
                s.Cpu += a.Cpu * r.Seconds; s.MaxCpu = Math.Max(s.MaxCpu, a.Cpu);
                s.Ram += a.Private; s.RamTicks++; s.MaxRam = Math.Max(s.MaxRam, a.Private);
                s.MaxCommit = Math.Max(s.MaxCommit, a.Commit);
                s.Gpu += a.Gpu * r.Seconds; s.MaxGpu = Math.Max(s.MaxGpu, a.Gpu);
                s.MaxVram = Math.Max(s.MaxVram, a.VramDedicated);
                s.NetRx += (long)(a.NetRx * r.Seconds); s.NetTx += (long)(a.NetTx * r.Seconds);
                s.DiskRead += (long)(a.DiskRead * r.Seconds); s.DiskWrite += (long)(a.DiskWrite * r.Seconds);
                s.Last = r.Ts;
                s.Flags |= a.Flags;
            }
        }

        var list = new List<AppSummary>(acc.Count);
        foreach (var (id, s) in acc)
        {
            var (name, path) = names(id);
            list.Add(new AppSummary
            {
                AppId = id, Name = name, ExePath = path, Flags = s.Flags,
                AvgCpu = totalSeconds > 0 ? s.Cpu / totalSeconds : 0, MaxCpu = s.MaxCpu,
                AvgRam = s.RamTicks > 0 ? (double)s.Ram / s.RamTicks : 0, MaxRam = s.MaxRam, MaxCommit = s.MaxCommit,
                AvgGpu = totalSeconds > 0 ? s.Gpu / totalSeconds : 0, MaxGpu = s.MaxGpu, MaxVram = s.MaxVram,
                NetRxBytes = s.NetRx, NetTxBytes = s.NetTx, DiskReadBytes = s.DiskRead, DiskWriteBytes = s.DiskWrite,
                LastSeenUnix = s.Last,
            });
        }
        return list;
    }

    private sealed class SummaryAcc
    {
        public double Cpu, Gpu, MaxCpu, MaxGpu, MaxVram, MaxRam, MaxCommit;
        public long Ram, NetRx, NetTx, DiskRead, DiskWrite, Last;
        public int RamTicks;
        public AppFlags Flags;
    }
}
