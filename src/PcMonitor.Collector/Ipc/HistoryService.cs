using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Native;
using PcMonitor.Collector.Storage;
using PcMonitor.Core;

namespace PcMonitor.Collector.Ipc;

internal static class SeriesMath
{
    /// <summary>Averages points into fixed buckets (peaks use max). Used to downsample the 1 s ring buffer.</summary>
    public static List<SeriesPoint> Bucketize(IReadOnlyList<SeriesPoint> points, int bucketSeconds)
    {
        if (bucketSeconds <= 1) return points.ToList();
        var result = new List<SeriesPoint>();
        int i = 0;
        while (i < points.Count)
        {
            long bucket = points[i].Ts / bucketSeconds * bucketSeconds;
            var p = new SeriesPoint { Ts = bucket };
            int n = 0;
            while (i < points.Count && points[i].Ts / bucketSeconds * bucketSeconds == bucket)
            {
                var s = points[i++];
                p.Cpu += s.Cpu; p.Ram += s.Ram; p.Gpu += s.Gpu; p.Vram += s.Vram;
                p.NetRx += s.NetRx; p.NetTx += s.NetTx; p.DiskRead += s.DiskRead; p.DiskWrite += s.DiskWrite;
                p.CpuMax = Math.Max(p.CpuMax, s.CpuMax); p.GpuMax = Math.Max(p.GpuMax, s.GpuMax);
                n++;
            }
            p.Cpu /= n; p.Ram /= n; p.Gpu /= n; p.Vram /= n;
            p.NetRx /= n; p.NetTx /= n; p.DiskRead /= n; p.DiskWrite /= n;
            result.Add(p);
        }
        return result;
    }

    /// <summary>Combines two per-app summaries covering adjacent time spans (weights = covered seconds).</summary>
    public static List<AppSummary> MergeAdjacent(List<AppSummary> first, double firstSeconds, List<AppSummary> second, double secondSeconds)
    {
        double total = Math.Max(1e-9, firstSeconds + secondSeconds);
        var map = first.ToDictionary(a => a.AppId);
        foreach (var b in second)
        {
            if (!map.TryGetValue(b.AppId, out var a)) { map[b.AppId] = Scale(b, secondSeconds / total); continue; }
            a.AvgCpu = (a.AvgCpu * firstSeconds + b.AvgCpu * secondSeconds) / total;
            a.AvgGpu = (a.AvgGpu * firstSeconds + b.AvgGpu * secondSeconds) / total;
            a.AvgRam = (a.AvgRam + b.AvgRam) / 2;          // level metric: mean of the two spans while alive
            a.MaxCpu = Math.Max(a.MaxCpu, b.MaxCpu); a.MaxGpu = Math.Max(a.MaxGpu, b.MaxGpu);
            a.MaxRam = Math.Max(a.MaxRam, b.MaxRam); a.MaxCommit = Math.Max(a.MaxCommit, b.MaxCommit);
            a.MaxVram = Math.Max(a.MaxVram, b.MaxVram);
            a.NetRxBytes += b.NetRxBytes; a.NetTxBytes += b.NetTxBytes;
            a.DiskReadBytes += b.DiskReadBytes; a.DiskWriteBytes += b.DiskWriteBytes;
            a.LastSeenUnix = Math.Max(a.LastSeenUnix, b.LastSeenUnix);
            a.Flags |= b.Flags;
        }
        return map.Values.ToList();
    }

    private static AppSummary Scale(AppSummary s, double weight)
    {
        s.AvgCpu *= weight; s.AvgGpu *= weight;   // present only in the later span: earlier span counts as idle
        return s;
    }
}

/// <summary>Answers history/summary/detail queries from the ring buffer, the database and the live state.</summary>
internal sealed class HistoryService
{
    private readonly CollectorContext _ctx;

    public HistoryService(CollectorContext ctx) => _ctx = ctx;

    public LiveHistoryResponse GetLiveHistory() =>
        new() { Points = TickRing.ToSystemSeries(_ctx.Ring.Snapshot()) };

    private bool IsLiveRange(long from, long to, long now) =>
        to - from <= Resolution.LiveWindowSeconds + 5 && to >= now - 5;

    public SummaryResponse GetSummary(long from, long to)
    {
        long now = _ctx.Clock();
        to = Math.Min(to, now + 1);
        var response = new SummaryResponse { FromUnix = from, ToUnix = to };
        if (from >= to) return response;

        List<AppSummary> list;
        if (IsLiveRange(from, to, now))
        {
            list = TickRing.Summarize(_ctx.Ring.Snapshot(from, to), _ctx.Identity.GetNameAndPath);
        }
        else
        {
            long flushed = Interlocked.Read(ref _ctx.FlushedUntilUnix);
            long dbTo = flushed > 0 ? Math.Min(to, flushed) : to;
            list = _ctx.Db.GetSummary(from, dbTo);
            if (flushed > 0 && to > flushed)
            {
                var tail = _ctx.Ring.Snapshot(flushed, to);
                if (tail.Count > 0)
                {
                    var tailSummary = TickRing.Summarize(tail, _ctx.Identity.GetNameAndPath);
                    list = SeriesMath.MergeAdjacent(list, Math.Max(0, dbTo - from), tailSummary, tail.Sum(r => r.Seconds));
                }
            }
        }

        EnrichSummaries(list, Math.Max(1, to - from));
        response.Apps = list;
        return response;
    }

    private void EnrichSummaries(List<AppSummary> list, double rangeSeconds)
    {
        var live = _ctx.LastTick;
        long ramTotal = live?.System.RamTotalBytes ?? 0;
        foreach (var s in list)
        {
            var id = _ctx.Identity.GetIdentity(s.AppId);
            if (id != null)
            {
                if (string.IsNullOrEmpty(s.Name)) s.Name = id.Name;
                s.ExePath ??= string.IsNullOrEmpty(id.ExePath) ? null : id.ExePath;
                s.Publisher ??= id.Company ?? id.InstallPublisher;
                if (s.Signature == SignatureState.Unknown) s.Signature = id.Signature;
                s.Flags |= id.StaticFlags;
            }
            if (string.IsNullOrEmpty(s.Name)) s.Name = $"app {s.AppId:x}";

            if (live != null)
                foreach (var a in live.Apps)
                    if (a.AppId == s.AppId) { s.Flags |= a.Flags; break; }

            s.Impact = ImpactScore.Compute(s.AvgCpu, (long)s.AvgRam, ramTotal, s.AvgGpu,
                (long)((s.NetRxBytes + s.NetTxBytes) / rangeSeconds), (long)((s.DiskReadBytes + s.DiskWriteBytes) / rangeSeconds));
        }
        list.Sort((a, b) => b.Impact.CompareTo(a.Impact));
    }

    public SeriesResponse GetSeries(long from, long to, int bucketSeconds, long? appId)
    {
        long now = _ctx.Clock();
        to = Math.Min(to, now + 1);
        if (bucketSeconds <= 0) bucketSeconds = Resolution.PickBucketSeconds(to - from);

        List<SeriesPoint> points;
        if (from >= to) points = new();
        else if (IsLiveRange(from, to, now))
        {
            var recs = _ctx.Ring.Snapshot(from, to);
            var raw = appId == null ? TickRing.ToSystemSeries(recs) : TickRing.ToAppSeries(recs, appId.Value);
            points = SeriesMath.Bucketize(raw, bucketSeconds);
        }
        else points = _ctx.Db.GetSeries(from, to, bucketSeconds, appId);

        return new SeriesResponse { BucketSeconds = bucketSeconds, Points = points };
    }

    public AppDetailResponse GetDetail(long appId, long from, long to)
    {
        var identity = _ctx.Identity.GetIdentity(appId);
        if (identity != null) _ctx.Identity.RefreshStartup(identity);

        var response = new AppDetailResponse { Identity = identity, Series = GetSeries(from, to, 0, appId) };

        var last = _ctx.LastTick;
        if (last != null)
            foreach (var a in last.Apps)
                if (a.AppId == appId) { response.Live = LiveBroadcaster.ToUsage(a, _ctx.Identity.GetNameAndPath); break; }

        response.Processes = ListProcesses(appId);
        response.Connections = GetConnections(appId);
        return response;
    }

    private List<ProcessUsage> ListProcesses(long appId)
    {
        var list = new List<ProcessUsage>();
        var gpu = _ctx.Gpu.Latest;
        var userCtx = _ctx.User.Current;
        lock (_ctx.Table.SyncRoot)
        {
            double dt = _ctx.Table.ElapsedSeconds;
            foreach (var p in _ctx.Table.Entries)
            {
                if (p.AppId != appId) continue;
                gpu.ByPid.TryGetValue(p.Pid, out var g);
                var net = _ctx.Aggregator.GetPidRates(p.Pid);
                list.Add(new ProcessUsage
                {
                    Pid = p.Pid, ParentPid = p.ParentPid, Name = p.Name,
                    Label = _ctx.Services.Describe(p.Pid),
                    CpuPct = p.CpuPct, PrivateBytes = p.PrivateBytes, CommitBytes = p.CommitBytes,
                    GpuPct = g.Pct,
                    NetRxBps = net.Rx, NetTxBps = net.Tx,
                    DiskReadBps = _ctx.EtwAvailable() ? net.DiskRead : p.ReadBps,
                    DiskWriteBps = _ctx.EtwAvailable() ? net.DiskWrite : p.WriteBps,
                    ThreadCount = p.Threads, HandleCount = p.Handles,
                    StartTimeUnix = FileTimeToUnix(p.CreateTime),
                    HasVisibleWindow = userCtx?.VisibleWindowPids.Contains(p.Pid) ?? false,
                });
            }
        }
        // Command lines need a handle per process; do it outside the table lock.
        foreach (var pu in list) pu.CommandLine = NativeMethods.TryGetCommandLine(pu.Pid);
        list.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return list;
    }

    private static long FileTimeToUnix(long fileTime) => fileTime <= 0 ? 0 : (fileTime - 116444736000000000L) / 10_000_000L;

    public List<ConnectionInfo> GetConnections(long appId, int limit = 200)
    {
        var merged = new Dictionary<(string, int, string), ConnectionInfo>();
        foreach (var c in _ctx.Db.GetConnections(appId, limit))
            merged[(c.RemoteIp, c.Port, c.Proto)] = c;
        foreach (var p in _ctx.Aggregator.Connections.PeekPending(appId))
        {
            var key = (p.RemoteIp, p.Port, p.Proto);
            if (merged.TryGetValue(key, out var c))
            {
                c.Bytes += p.Bytes;
                c.LastSeenUnix = Math.Max(c.LastSeenUnix, p.LastSeen);
                c.FirstSeenUnix = c.FirstSeenUnix == 0 ? p.FirstSeen : Math.Min(c.FirstSeenUnix, p.FirstSeen);
            }
            else
                merged[key] = new ConnectionInfo { RemoteIp = p.RemoteIp, Port = p.Port, Proto = p.Proto, Bytes = p.Bytes, FirstSeenUnix = p.FirstSeen, LastSeenUnix = p.LastSeen };
        }

        var list = merged.Values.OrderByDescending(c => c.LastSeenUnix).ThenByDescending(c => c.Bytes).Take(limit).ToList();
        foreach (var c in list) c.RemoteHost = _ctx.Dns.TryGet(c.RemoteIp);
        return list;
    }
}
