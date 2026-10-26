using System.Collections.Concurrent;
using System.Net;

namespace PcMonitor.Collector.Ipc;

/// <summary>
/// Lazy, cached reverse DNS. <see cref="TryGet"/> never blocks: it returns what is known and schedules a lookup,
/// so the UI shows host names on its next refresh.
/// </summary>
internal sealed class ReverseDns
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly SemaphoreSlim _limit = new(4);
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    public const int MaxEntries = 5000;
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    public string? TryGet(string ip)
    {
        if (_cache.TryGetValue(ip, out var host)) return host;
        if (_cache.Count < MaxEntries && IPAddress.TryParse(ip, out var addr) && ShouldResolve(addr) && _pending.TryAdd(ip, 0))
            _ = LookupAsync(ip, addr);
        return null;
    }

    private static bool ShouldResolve(IPAddress a) =>
        !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast;

    private async Task LookupAsync(string ip, IPAddress addr)
    {
        await _limit.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(LookupTimeout);
            string? name = null;
            try
            {
                var entry = await Dns.GetHostEntryAsync(addr.ToString(), cts.Token).ConfigureAwait(false);
                name = entry.HostName;
                if (name == ip) name = null;
            }
            catch { /* NXDOMAIN, timeout, offline ... */ }
            _cache[ip] = name;
        }
        finally
        {
            _limit.Release();
            _pending.TryRemove(ip, out _);
        }
    }
}
