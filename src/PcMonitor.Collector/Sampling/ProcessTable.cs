using System.Diagnostics;
using PcMonitor.Collector.Native;
using PcMonitor.Core;

namespace PcMonitor.Collector.Sampling;

/// <summary>Stable, mutable record for one live process; updated in place every tick to avoid allocation.</summary>
internal sealed class ProcessEntry
{
    public int Pid;
    public int ParentPid;
    public long CreateTime;
    public string Name = "";
    public string? Path;
    public long AppId;
    public int SessionId;
    public int Generation;

    public long CpuTicksPrev;
    public long CpuTicksNow;
    public long ReadPrev, ReadNow, WritePrev, WriteNow;

    /// <summary>Percent of total machine capacity during the last tick.</summary>
    public double CpuPct;
    public long ReadBps, WriteBps;
    public long PrivateBytes;
    public long CommitBytes;
    public int Threads;
    public int Handles;

    public bool HasPath => Path != null;
}

internal readonly record struct DeadProcess(long AppId, long ExpiresAtTick);

/// <summary>
/// Keeps the live process set and turns raw counters into per-tick rates. Everything per tick is one
/// native call plus a dictionary pass over ~300 entries.
/// </summary>
internal sealed class ProcessTable : IDisposable
{
    private readonly ProcessReader _reader = new();
    private readonly Dictionary<int, ProcessEntry> _entries = new(512);
    private readonly Dictionary<int, DeadProcess> _recentlyDead = new();
    private readonly List<int> _scratchRemove = new();
    private long _lastTimestamp;
    private int _generation;
    private bool _firstPass = true;

    /// <summary>How long a dead pid still receives late ETW events.</summary>
    public static readonly TimeSpan DeadRetention = TimeSpan.FromSeconds(5);

    /// <summary>The tick loop holds this while updating; readers on other threads (pipe handlers) lock it to inspect entries.</summary>
    public object SyncRoot { get; } = new();

    public int LogicalCores { get; }
    public double ElapsedSeconds { get; private set; } = 1;
    public IReadOnlyCollection<ProcessEntry> Entries => _entries.Values;

    public ProcessTable()
    {
        LogicalCores = (int)Math.Max(1, NativeMethods.GetActiveProcessorCount(NativeMethods.ALL_PROCESSOR_GROUPS));
    }

    public bool TryGet(int pid, out ProcessEntry entry) => _entries.TryGetValue(pid, out entry!);

    /// <summary>Resolve a pid (alive, or died within <see cref="DeadRetention"/>) to its application.</summary>
    public bool TryGetAppId(int pid, out long appId)
    {
        if (_entries.TryGetValue(pid, out var e)) { appId = e.AppId; return true; }
        if (_recentlyDead.TryGetValue(pid, out var d)) { appId = d.AppId; return true; }
        appId = 0;
        return false;
    }

    public bool Update()
    {
        if (!_reader.Snapshot()) return false;

        long now = Stopwatch.GetTimestamp();
        double dt = _firstPass ? 1.0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        if (dt < 0.05) dt = 0.05;
        ElapsedSeconds = dt;
        _lastTimestamp = now;
        int gen = ++_generation;
        // CPU ticks are 100 ns; machine capacity per second is cores * 1e7 ticks.
        double capacityTicks = dt * 1e7 * LogicalCores;

        foreach (var r in _reader.Records())
        {
            if (r.Pid == 0) continue; // System Idle Process: not an app

            if (!_entries.TryGetValue(r.Pid, out var e) || e.CreateTime != r.CreateTime)
            {
                e = CreateEntry(r);
                _entries[r.Pid] = e;
                if (_firstPass)
                {
                    // Unknown history: start deltas from "now" so we don't report lifetime CPU as one tick.
                    e.CpuTicksPrev = r.CpuTime; e.ReadPrev = r.ReadBytes; e.WritePrev = r.WriteBytes;
                }
                // else: process started since last tick, so its whole lifetime is inside this tick (prev = 0).
            }
            else
            {
                e.CpuTicksPrev = e.CpuTicksNow; e.ReadPrev = e.ReadNow; e.WritePrev = e.WriteNow;
            }

            e.Generation = gen;
            e.ParentPid = r.ParentPid;
            e.CpuTicksNow = r.CpuTime; e.ReadNow = r.ReadBytes; e.WriteNow = r.WriteBytes;
            e.CpuPct = CpuCalculator.Percent(e.CpuTicksNow - e.CpuTicksPrev, capacityTicks);
            e.ReadBps = (long)Math.Max(0, (e.ReadNow - e.ReadPrev) / dt);
            e.WriteBps = (long)Math.Max(0, (e.WriteNow - e.WritePrev) / dt);
            e.PrivateBytes = r.PrivateWorkingSet > 0 ? r.PrivateWorkingSet : r.WorkingSet;
            e.CommitBytes = r.Commit;
            e.Threads = r.Threads;
            e.Handles = r.Handles;
            e.SessionId = r.SessionId;
        }

        // Anything not stamped with this generation has exited.
        _scratchRemove.Clear();
        foreach (var kv in _entries)
            if (kv.Value.Generation != gen) _scratchRemove.Add(kv.Key);

        long expires = Environment.TickCount64 + (long)DeadRetention.TotalMilliseconds;
        foreach (int pid in _scratchRemove)
        {
            _recentlyDead[pid] = new DeadProcess(_entries[pid].AppId, expires);
            _entries.Remove(pid);
        }

        if (_recentlyDead.Count > 0)
        {
            long tick = Environment.TickCount64;
            _scratchRemove.Clear();
            foreach (var kv in _recentlyDead)
                if (kv.Value.ExpiresAtTick <= tick) _scratchRemove.Add(kv.Key);
            foreach (int pid in _scratchRemove) _recentlyDead.Remove(pid);
        }

        _firstPass = false;
        return true;
    }

    private static unsafe ProcessEntry CreateEntry(in RawProcess r)
    {
        string name = r.NameString();
        if (name.Length == 0) name = r.Pid == 4 ? "System" : $"pid{r.Pid}";
        string? path = r.Pid == 4 ? null : NativeMethods.TryGetImagePath(r.Pid);
        return new ProcessEntry
        {
            Pid = r.Pid,
            ParentPid = r.ParentPid,
            CreateTime = r.CreateTime,
            Name = name,
            Path = path,
            AppId = AppKey.FromPath(path ?? "?" + name.ToLowerInvariant()),
            SessionId = r.SessionId,
        };
    }

    public void Dispose() => _reader.Dispose();
}

internal static class CpuCalculator
{
    /// <summary>CPU time delta (100 ns ticks) as percent of total machine capacity, clamped 0..100.</summary>
    public static double Percent(long deltaTicks, double capacityTicks)
    {
        if (deltaTicks <= 0 || capacityTicks <= 0) return 0;
        return Math.Min(100.0, deltaTicks * 100.0 / capacityTicks);
    }
}
