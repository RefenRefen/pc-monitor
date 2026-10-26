using System.Threading.Channels;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Core;

namespace PcMonitor.Collector.Ipc;

internal sealed class Subscriber
{
    public LiveDetail Detail { get; init; }
    public int TopN { get; init; }
    /// <summary>Holds at most two frames; a slow client loses old frames instead of growing memory.</summary>
    public Channel<LiveFrame> Frames { get; } = Channel.CreateBounded<LiveFrame>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
}

/// <summary>
/// Fans each tick out to subscribed widgets. With no subscribers (widget hidden) it does nothing at all,
/// so an idle system pays no serialisation or allocation cost for the UI.
/// </summary>
internal sealed class LiveBroadcaster
{
    private readonly object _gate = new();
    private List<Subscriber> _subscribers = new();

    public int Count { get { lock (_gate) return _subscribers.Count; } }

    public Subscriber Subscribe(LiveDetail detail, int topN)
    {
        var s = new Subscriber { Detail = detail, TopN = Math.Clamp(topN, 1, 50) };
        lock (_gate) _subscribers = new List<Subscriber>(_subscribers) { s };
        return s;
    }

    public void Unsubscribe(Subscriber s)
    {
        lock (_gate) { _subscribers = _subscribers.Where(x => x != s).ToList(); }
        s.Frames.Writer.TryComplete();
    }

    public void Publish(TickResult tick, Func<long, (string Name, string? Path)> names)
    {
        List<Subscriber> subs;
        lock (_gate) subs = _subscribers;
        if (subs.Count == 0) return;

        LiveFrame? full = null;
        var compact = new Dictionary<int, LiveFrame>();
        foreach (var s in subs)
        {
            LiveFrame frame;
            if (s.Detail == LiveDetail.Full)
                frame = full ??= BuildFrame(tick, names, int.MaxValue);
            else if (!compact.TryGetValue(s.TopN, out frame!))
                compact[s.TopN] = frame = BuildFrame(tick, names, s.TopN);
            s.Frames.Writer.TryWrite(frame);
        }
    }

    internal static LiveFrame BuildFrame(TickResult tick, Func<long, (string Name, string? Path)> names, int topN)
    {
        IEnumerable<AppTick> apps = tick.Apps;
        if (topN != int.MaxValue) apps = apps.OrderByDescending(a => a.Impact).Take(topN);

        var list = new List<AppUsage>();
        foreach (var a in apps) list.Add(ToUsage(a, names));
        return new LiveFrame
        {
            Ts = tick.Ts, System = tick.System, Apps = list,
            EtwAvailable = tick.EtwAvailable, GpuAvailable = tick.GpuAvailable,
        };
    }

    internal static AppUsage ToUsage(in AppTick a, Func<long, (string Name, string? Path)> names)
    {
        var (name, path) = names(a.AppId);
        return new AppUsage
        {
            AppId = a.AppId, Name = name, ExePath = path,
            CpuPct = a.Cpu, PrivateBytes = a.Private, CommitBytes = a.Commit,
            GpuPct = a.Gpu, VramDedicatedBytes = a.VramDedicated, VramSharedBytes = a.VramShared,
            NetRxBps = a.NetRx, NetTxBps = a.NetTx, DiskReadBps = a.DiskRead, DiskWriteBps = a.DiskWrite,
            ProcessCount = a.Processes, ThreadCount = a.Threads, HandleCount = a.Handles,
            Impact = a.Impact, Flags = a.Flags,
        };
    }
}
