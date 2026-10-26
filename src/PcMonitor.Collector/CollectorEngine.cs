using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Identity;
using PcMonitor.Collector.Sampling;
using PcMonitor.Collector.Storage;
using PcMonitor.Core;

namespace PcMonitor.Collector;

/// <summary>
/// Owns every sampler and store, and performs one collection tick at a time. Separated from the hosting code so
/// tests (and the console mode) can drive it without a timer.
/// </summary>
internal sealed class CollectorEngine : IDisposable
{
    public const string RetentionMetaKey = "last_retention_unix";
    private static readonly long RetentionIntervalSeconds = 23 * 3600;

    private readonly SystemSampler _system = new();
    private readonly EtwCounters _etwCounters = new();
    private readonly EtwMonitor _etw;
    private readonly GpuSampler _gpu;
    private readonly MinuteAccumulator _minutes = new();
    private readonly ILogger _log;
    private int _tickNo;
    private bool _disposed;

    public CollectorContext Context { get; }

    public CollectorEngine(string databasePath, ILogger log, bool startEtw = true, bool autoStartIdentity = true)
    {
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        var db = new UsageDatabase(databasePath);
        var identity = new IdentityResolver(db, log, autoStart: autoStartIdentity);
        var table = new ProcessTable();
        var user = new UserContext();
        _gpu = new GpuSampler(log);
        _etw = new EtwMonitor(_etwCounters, log);

        var aggregator = new AppAggregator(table, _system, _etwCounters, () => _etw.IsRunning, _gpu, identity, user);
        Context = new CollectorContext
        {
            Db = db,
            Identity = identity,
            Ring = new TickRing(),
            Table = table,
            Aggregator = aggregator,
            Gpu = _gpu,
            User = user,
            Log = log,
            EtwAvailable = () => _etw.IsRunning,
            IsElevated = IsElevated(),
        };

        if (startEtw)
        {
            if (_etw.TryStart()) log.LogInformation("ETW kernel session started");
            else log.LogWarning("ETW unavailable ({Reason}); per-app network and disk throughput will be limited", _etw.FailureReason);
        }

        RunRetentionIfDue();
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) || id.IsSystem;
    }

    /// <summary>Samples everything once, stores the result and publishes it to subscribed widgets.</summary>
    public TickResult Tick()
    {
        int n = ++_tickNo;
        var ctx = Context;

        // GPU counters are comparatively expensive to read, and utilisation is smooth: every other second is enough.
        if (n % 2 == 1) _gpu.Sample();
        if (n % 60 == 0) _etw.RefreshLocalAddresses();

        TickResult tick;
        lock (ctx.Table.SyncRoot)
        {
            ctx.Table.Update();
            _system.Sample();
            tick = ctx.Aggregator.Tick();
        }

        ctx.Ring.Append(new TickRecord(tick.Ts, tick.Seconds, tick.System, tick.Apps));
        ctx.LastTick = tick;

        var batch = _minutes.Add(tick);
        if (batch != null) Persist(batch);

        ctx.Broadcaster.Publish(tick, ctx.Identity.GetNameAndPath);
        return tick;
    }

    /// <summary>Writes the partially accumulated minute (used at shutdown so nothing is lost).</summary>
    public void FlushPartialMinute()
    {
        var batch = _minutes.Flush();
        if (batch != null) Persist(batch);
    }

    private void Persist(MinuteBatch batch)
    {
        try
        {
            Context.Db.WriteMinute(batch);
            Context.FlushedUntilUnix = batch.Ts + 60;
            var pending = Context.Aggregator.Connections.TakePending();
            if (pending.Count > 0) Context.Db.UpsertConnections(pending);
            if (batch.Ts % 3600 < 60) RunRetentionIfDue();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Writing minute {Minute} failed", batch.Ts);
        }
    }

    private void RunRetentionIfDue()
    {
        try
        {
            long now = TimeUtil.NowUnix();
            long last = long.TryParse(Context.Db.GetMeta(RetentionMetaKey), out var v) ? v : 0;
            if (now - last < RetentionIntervalSeconds) return;
            var sw = Stopwatch.StartNew();
            var result = Context.Db.RunRetention(now);
            Context.Db.SetMeta(RetentionMetaKey, now.ToString());
            _log.LogInformation("Retention finished in {Ms} ms: {Result}", sw.ElapsedMilliseconds, result);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Retention run failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { FlushPartialMinute(); } catch { /* best effort */ }
        _etw.Dispose();
        _gpu.Dispose();
        Context.Identity.Dispose();
        Context.Table.Dispose();
        Context.Db.Dispose();
    }
}
