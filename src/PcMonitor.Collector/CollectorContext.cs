using Microsoft.Extensions.Logging;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Identity;
using PcMonitor.Collector.Ipc;
using PcMonitor.Collector.Native;
using PcMonitor.Collector.Sampling;
using PcMonitor.Collector.Storage;

namespace PcMonitor.Collector;

/// <summary>Everything the pipe handlers need, wired once at start-up.</summary>
internal sealed class CollectorContext
{
    public required UsageDatabase Db { get; init; }
    public required IdentityResolver Identity { get; init; }
    public required TickRing Ring { get; init; }
    public required ProcessTable Table { get; init; }
    public required AppAggregator Aggregator { get; init; }
    public required IGpuSource Gpu { get; init; }
    public required UserContext User { get; init; }
    public required ILogger Log { get; init; }
    public required Func<bool> EtwAvailable { get; init; }
    public LiveBroadcaster Broadcaster { get; } = new();
    public ServiceMap Services { get; } = new();
    public ReverseDns Dns { get; } = new();
    public Func<long> Clock { get; init; } = PcMonitor.Core.TimeUtil.NowUnix;
    public bool IsElevated { get; init; }

    /// <summary>End (exclusive) of the last minute written to the database; ring data after this is not yet persisted.</summary>
    public long FlushedUntilUnix;

    private TickResult? _lastTick;
    public TickResult? LastTick { get => Volatile.Read(ref _lastTick); set => Volatile.Write(ref _lastTick, value); }
}
