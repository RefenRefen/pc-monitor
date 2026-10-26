using PcMonitor.Collector.Sampling;

namespace PcMonitor.Collector.Aggregation;

public readonly record struct ConnectionRow(long AppId, string RemoteIp, int Port, string Proto, long Bytes, long FirstSeen, long LastSeen);

/// <summary>
/// Remembers which remote endpoints each app talked to: a rolling "distinct hosts" view for suspicion
/// detection and a pending batch flushed to SQLite every minute. Tick-thread only.
/// </summary>
internal sealed class ConnectionTracker
{
    public const int RecentWindowSeconds = 600;
    public const int MaxHostsPerApp = 5000;

    private readonly Dictionary<long, Dictionary<IpKey, long>> _recentHosts = new();
    private readonly Dictionary<(long AppId, IpKey Ip, int Port, bool Udp), (long Bytes, long First, long Last)> _pending = new();
    private readonly object _gate = new();
    private long _lastPrune;

    public void Add(in AppConnDelta d, long nowUnix)
    {
        lock (_gate) AddLocked(d, nowUnix);
    }

    private void AddLocked(in AppConnDelta d, long nowUnix)
    {
        if (!_recentHosts.TryGetValue(d.AppId, out var hosts))
            _recentHosts[d.AppId] = hosts = new Dictionary<IpKey, long>();
        if (hosts.Count < MaxHostsPerApp || hosts.ContainsKey(d.Remote)) hosts[d.Remote] = nowUnix;

        var key = (d.AppId, d.Remote, d.Port, d.Udp);
        _pending[key] = _pending.TryGetValue(key, out var cur)
            ? (cur.Bytes + d.Bytes, cur.First, nowUnix)
            : (d.Bytes, nowUnix, nowUnix);
    }

    public int DistinctHosts(long appId)
    {
        lock (_gate) return _recentHosts.TryGetValue(appId, out var h) ? h.Count : 0;
    }

    public void Prune(long nowUnix)
    {
        lock (_gate) PruneLocked(nowUnix);
    }

    private void PruneLocked(long nowUnix)
    {
        if (nowUnix - _lastPrune < 30) return;
        _lastPrune = nowUnix;
        long cutoff = nowUnix - RecentWindowSeconds;
        List<long>? empty = null;
        foreach (var (appId, hosts) in _recentHosts)
        {
            List<IpKey>? stale = null;
            foreach (var kv in hosts)
                if (kv.Value < cutoff) (stale ??= new()).Add(kv.Key);
            if (stale != null) foreach (var k in stale) hosts.Remove(k);
            if (hosts.Count == 0) (empty ??= new()).Add(appId);
        }
        if (empty != null) foreach (var id in empty) _recentHosts.Remove(id);
    }

    /// <summary>Takes the batch accumulated since the previous call.</summary>
    public List<ConnectionRow> TakePending()
    {
        lock (_gate) return TakePendingLocked();
    }

    private List<ConnectionRow> TakePendingLocked()
    {
        var rows = new List<ConnectionRow>(_pending.Count);
        foreach (var (k, v) in _pending)
            rows.Add(new ConnectionRow(k.AppId, k.Ip.ToString(), k.Port, k.Udp ? "udp" : "tcp", v.Bytes, v.First, v.Last));
        _pending.Clear();
        return rows;
    }

    /// <summary>Live (not yet flushed) rows for one app, so detail views include the current minute.</summary>
    public List<ConnectionRow> PeekPending(long appId)
    {
        var rows = new List<ConnectionRow>();
        lock (_gate)
            foreach (var (k, v) in _pending)
                if (k.AppId == appId)
                    rows.Add(new ConnectionRow(k.AppId, k.Ip.ToString(), k.Port, k.Udp ? "udp" : "tcp", v.Bytes, v.First, v.Last));
        return rows;
    }
}
