using PcMonitor.Collector.Sampling;
using PcMonitor.Core;

namespace PcMonitor.Collector.Aggregation;

/// <summary>Compact per-app, per-tick record kept in the ring buffer and fed to minute accumulators.</summary>
internal struct AppTick
{
    public long AppId;
    public double Cpu;
    public long Private;
    public long Commit;
    public double Gpu;
    public long VramDedicated;
    public long VramShared;
    public long NetRx, NetTx, DiskRead, DiskWrite;
    public int Processes, Threads, Handles;
    public double Impact;
    public AppFlags Flags;

    public readonly long NetBytes => NetRx + NetTx;
    public readonly long DiskBytes => DiskRead + DiskWrite;

    public const long RamThresholdBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Whether an app is worth recording this tick. Keeps the history small: idle small processes
    /// are skipped, anything using noticeable CPU/GPU/IO or lots of memory is kept.
    /// </summary>
    public readonly bool IsSignificant =>
        Cpu >= 0.05 || Gpu >= 0.5 || NetBytes + DiskBytes >= 1024 || Private >= RamThresholdBytes;
}

internal readonly record struct AppConnDelta(long AppId, IpKey Remote, int Port, bool Udp, long Bytes);

internal sealed class TickResult
{
    public long Ts;
    public double Seconds;
    public SystemTotals System = new();
    /// <summary>Every app that had at least one process this tick.</summary>
    public AppTick[] Apps = Array.Empty<AppTick>();
    public List<AppConnDelta> Connections = new();
    public bool EtwAvailable;
    public bool GpuAvailable;
}

/// <summary>Identity data the aggregator needs (names, static flags); implemented by the identity resolver.</summary>
internal interface IAppMetadata
{
    /// <summary>Called when an app is seen for the first time (or its path becomes known) so it can be resolved lazily.</summary>
    void Observe(long appId, string? exePath, string processName);
    string? GetDisplayName(long appId);
    AppFlags GetStaticFlags(long appId);
}

/// <summary>Window/idle state reported by the widget (the service cannot see the user desktop).</summary>
internal sealed class UserContext
{
    public sealed record Snapshot(HashSet<int> VisibleWindowPids, int IdleSeconds, long ReportedAtTick);

    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    private volatile Snapshot? _snapshot;

    public void Report(IEnumerable<int> pids, int idleSeconds) =>
        _snapshot = new Snapshot(new HashSet<int>(pids), idleSeconds, Environment.TickCount64);

    /// <summary>Returns null while no recent report exists (window state unknown).</summary>
    public Snapshot? Current
    {
        get
        {
            var s = _snapshot;
            return s != null && Environment.TickCount64 - s.ReportedAtTick <= Freshness.TotalMilliseconds ? s : null;
        }
    }
}
