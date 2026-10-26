using PcMonitor.Collector.Sampling;
using PcMonitor.Core;

namespace PcMonitor.Collector.Aggregation;

/// <summary>
/// Turns one process snapshot + GPU + ETW deltas into per-app numbers. Usage is grouped per executable
/// path (not per PID) so restarts and multi-process apps (Chrome, svchost) add up into one entry.
/// </summary>
internal sealed class AppAggregator
{
    private sealed class AppAcc
    {
        public long AppId;
        public double Cpu;
        public long Private, Commit;
        public double Gpu;
        public long VramDed, VramShared;
        public long NetRx, NetTx, DiskRead, DiskWrite;
        public long IoReadBps, IoWriteBps;       // process IO counters (fallback when ETW is unavailable)
        public int Processes, Threads, Handles;
        public bool HasWindow;
        public int Seen;                           // tick sequence in which the app was last present
        public readonly SuspicionState Suspicion = new();
    }

    private readonly ProcessTable _processes;
    private readonly SystemSampler _system;
    private readonly EtwCounters _etw;
    private readonly Func<bool> _etwAvailable;
    private readonly IGpuSource _gpu;
    private readonly IAppMetadata _meta;
    private readonly UserContext _user;
    private readonly Func<long> _clockUnix;

    private readonly Dictionary<long, AppAcc> _apps = new(256);
    private readonly List<PidDelta> _pidDeltas = new();
    private readonly List<ConnDelta> _connDeltas = new();
    private readonly List<long> _scratchRemove = new();
    private int _tickSeq;

    public ConnectionTracker Connections { get; } = new();

    private double _lastDt = 1;

    /// <summary>Per-process ETW rates of the last tick (caller must hold the process table lock).</summary>
    public (long Rx, long Tx, long DiskRead, long DiskWrite) GetPidRates(int pid)
    {
        foreach (var d in _pidDeltas)
            if (d.Pid == pid)
                return ((long)(d.Rx / _lastDt), (long)(d.Tx / _lastDt), (long)(d.DiskRead / _lastDt), (long)(d.DiskWrite / _lastDt));
        return default;
    }

    public AppAggregator(ProcessTable processes, SystemSampler system, EtwCounters etw, Func<bool> etwAvailable,
        IGpuSource gpu, IAppMetadata meta, UserContext user, Func<long>? clockUnix = null)
    {
        _processes = processes;
        _system = system;
        _etw = etw;
        _etwAvailable = etwAvailable;
        _gpu = gpu;
        _meta = meta;
        _user = user;
        _clockUnix = clockUnix ?? TimeUtil.NowUnix;
    }

    public TickResult Tick()
    {
        int seq = ++_tickSeq;
        long nowUnix = _clockUnix();
        double dt = _processes.ElapsedSeconds;
        _lastDt = dt;
        bool etwOn = _etwAvailable();
        var gpu = _gpu.Latest;
        var ctx = _user.Current;

        // 1) Process counters -> per app.
        long ioReadTotal = 0, ioWriteTotal = 0;
        foreach (var p in _processes.Entries)
        {
            if (!_apps.TryGetValue(p.AppId, out var a))
            {
                _apps[p.AppId] = a = new AppAcc { AppId = p.AppId };
                _meta.Observe(p.AppId, p.Path, p.Name);
            }
            else if (a.Seen != seq && a.Seen != seq - 1 && p.Path != null)
            {
                _meta.Observe(p.AppId, p.Path, p.Name); // came back after a gap; cheap no-op if known
            }

            if (a.Seen != seq)
            {
                a.Seen = seq;
                a.Cpu = 0; a.Private = 0; a.Commit = 0; a.Gpu = 0; a.VramDed = 0; a.VramShared = 0;
                a.NetRx = a.NetTx = a.DiskRead = a.DiskWrite = 0;
                a.IoReadBps = a.IoWriteBps = 0;
                a.Processes = a.Threads = a.Handles = 0;
                a.HasWindow = false;
            }

            a.Cpu += p.CpuPct;
            a.Private += p.PrivateBytes;
            a.Commit += p.CommitBytes;
            a.Processes++;
            a.Threads += p.Threads;
            a.Handles += p.Handles;
            a.IoReadBps += p.ReadBps;
            a.IoWriteBps += p.WriteBps;
            ioReadTotal += p.ReadBps;
            ioWriteTotal += p.WriteBps;
            if (ctx != null && ctx.VisibleWindowPids.Contains(p.Pid)) a.HasWindow = true;

            if (gpu.Available && gpu.ByPid.TryGetValue(p.Pid, out var g))
            {
                a.Gpu += g.Pct;
                a.VramDed += g.VramDedicated;
                a.VramShared += g.VramShared;
            }
        }

        // 2) ETW deltas (exact network + physical disk bytes) -> per app.
        _etw.Drain(_pidDeltas, _connDeltas, out long sysRx, out long sysTx, out long sysDiskR, out long sysDiskW);
        var result = new TickResult { Ts = nowUnix, Seconds = dt, EtwAvailable = etwOn, GpuAvailable = gpu.Available };
        foreach (var d in _pidDeltas)
        {
            if (_processes.TryGetAppId(d.Pid, out long appId) && _apps.TryGetValue(appId, out var a))
            {
                if (a.Seen != seq) continue; // app already gone; bytes still counted in system totals
                a.NetRx += d.Rx; a.NetTx += d.Tx; a.DiskRead += d.DiskRead; a.DiskWrite += d.DiskWrite;
            }
        }
        foreach (var c in _connDeltas)
        {
            if (_processes.TryGetAppId(c.Key.Pid, out long appId))
            {
                var delta = new AppConnDelta(appId, c.Key.Remote, c.Key.Port, c.Key.Udp, c.Bytes);
                result.Connections.Add(delta);
                Connections.Add(delta, nowUnix);
            }
        }
        Connections.Prune(nowUnix);

        // 3) System totals.
        var sys = result.System;
        sys.CpuPct = _system.CpuPct;
        sys.RamTotalBytes = _system.RamTotalBytes;
        sys.RamUsedBytes = _system.RamUsedBytes;
        sys.GpuPct = gpu.Available ? gpu.TotalPct : 0;
        sys.VramUsedBytes = gpu.Available ? gpu.TotalVramBytes : 0;
        sys.ProcessCount = _processes.Entries.Count;
        if (etwOn)
        {
            sys.NetRxBps = (long)(sysRx / dt);
            sys.NetTxBps = (long)(sysTx / dt);
            sys.DiskReadBps = (long)(sysDiskR / dt);
            sys.DiskWriteBps = (long)(sysDiskW / dt);
        }
        else
        {
            sys.DiskReadBps = ioReadTotal;   // approximation: includes network/device IO
            sys.DiskWriteBps = ioWriteTotal;
        }

        // 4) Per-app records.
        var list = new List<AppTick>(_apps.Count);
        _scratchRemove.Clear();
        foreach (var a in _apps.Values)
        {
            if (a.Seen != seq)
            {
                // Forget apps that have been gone a while; their suspicion state is no longer meaningful.
                if (seq - a.Seen > 120) _scratchRemove.Add(a.AppId);
                continue;
            }

            long rx = (long)(a.NetRx / dt), tx = (long)(a.NetTx / dt);
            long dr = etwOn ? (long)(a.DiskRead / dt) : a.IoReadBps;
            long dw = etwOn ? (long)(a.DiskWrite / dt) : a.IoWriteBps;
            double cpu = Math.Min(100, a.Cpu);
            double gpuPct = Math.Min(100, a.Gpu);

            AppFlags dynamic = a.Suspicion.Update(new SuspicionInput(
                cpu, gpuPct, rx + tx,
                ctx == null ? null : a.HasWindow,
                ctx?.IdleSeconds,
                Connections.DistinctHosts(a.AppId)));

            list.Add(new AppTick
            {
                AppId = a.AppId,
                Cpu = cpu,
                Private = a.Private,
                Commit = a.Commit,
                Gpu = gpuPct,
                VramDedicated = a.VramDed,
                VramShared = a.VramShared,
                NetRx = rx, NetTx = tx, DiskRead = dr, DiskWrite = dw,
                Processes = a.Processes, Threads = a.Threads, Handles = a.Handles,
                Impact = ImpactScore.Compute(cpu, a.Private, _system.RamTotalBytes, gpuPct, rx + tx, dr + dw),
                Flags = dynamic | _meta.GetStaticFlags(a.AppId),
            });
        }
        foreach (long id in _scratchRemove) _apps.Remove(id);

        result.Apps = list.ToArray();
        return result;
    }
}
