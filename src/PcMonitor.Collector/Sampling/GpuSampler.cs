using System.Globalization;
using Microsoft.Extensions.Logging;
using PcMonitor.Collector.Native;

namespace PcMonitor.Collector.Sampling;

/// <summary>Parsing of PDH GPU instance names such as <c>pid_1234_luid_0x00000000_0x0000F3E5_phys_0_eng_0_engtype_3D</c>.</summary>
internal static class GpuInstanceName
{
    public static bool TryParse(ReadOnlySpan<char> name, out int pid, out ReadOnlySpan<char> luid, out ReadOnlySpan<char> engineType)
    {
        pid = 0; luid = default; engineType = default;

        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return false;
        var rest = name.Slice(4);
        int us = rest.IndexOf('_');
        if (us <= 0 || !int.TryParse(rest.Slice(0, us), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;

        int li = name.IndexOf("_luid_", StringComparison.Ordinal);
        if (li >= 0)
        {
            var afterLuid = name.Slice(li + 6);
            int end = afterLuid.IndexOf("_phys_", StringComparison.Ordinal);
            luid = end >= 0 ? afterLuid.Slice(0, end) : afterLuid;
        }

        int ei = name.IndexOf("_engtype_", StringComparison.Ordinal);
        if (ei >= 0) engineType = name.Slice(ei + 9);
        return true;
    }
}

/// <summary>
/// Builds a <see cref="GpuSnapshot"/> the way Task Manager reports it: per process, utilisation of each engine
/// type (3D, Copy, VideoDecode, ...) is summed over its engines and the busiest type wins.
/// </summary>
internal sealed class GpuSnapshotBuilder
{
    private readonly Dictionary<(int Pid, string Type), double> _perPidType = new();
    private readonly Dictionary<(string Luid, string Type), double> _perAdapterType = new();
    private readonly Dictionary<int, (long Dedicated, long Shared)> _memory = new();

    public void AddEngine(string instanceName, double utilisationPct)
    {
        if (!GpuInstanceName.TryParse(instanceName, out int pid, out var luid, out var type)) return;
        if (utilisationPct <= 0 || double.IsNaN(utilisationPct)) return;
        string t = type.IsEmpty ? "other" : string.Intern(type.ToString());
        var k1 = (pid, t);
        _perPidType[k1] = _perPidType.GetValueOrDefault(k1) + utilisationPct;
        var k2 = (luid.ToString(), t);
        _perAdapterType[k2] = _perAdapterType.GetValueOrDefault(k2) + utilisationPct;
    }

    public void AddDedicated(string instanceName, long bytes) => AddMemory(instanceName, bytes, shared: false);
    public void AddShared(string instanceName, long bytes) => AddMemory(instanceName, bytes, shared: true);

    private void AddMemory(string instanceName, long bytes, bool shared)
    {
        if (!GpuInstanceName.TryParse(instanceName, out int pid, out _, out _) || bytes <= 0) return;
        _memory.TryGetValue(pid, out var cur);
        _memory[pid] = shared ? (cur.Dedicated, cur.Shared + bytes) : (cur.Dedicated + bytes, cur.Shared);
    }

    public GpuSnapshot Build()
    {
        var byPid = new Dictionary<int, GpuPidUsage>();
        foreach (var ((pid, _), value) in _perPidType)
        {
            byPid.TryGetValue(pid, out var cur);
            // max across engine types
            byPid[pid] = cur with { Pct = Math.Max(cur.Pct, Math.Min(100, value)) };
        }

        long totalVram = 0;
        foreach (var (pid, mem) in _memory)
        {
            totalVram += mem.Dedicated;
            byPid.TryGetValue(pid, out var cur);
            byPid[pid] = cur with { VramDedicated = mem.Dedicated, VramShared = mem.Shared };
        }

        double total = 0;
        foreach (double v in _perAdapterType.Values) total = Math.Max(total, Math.Min(100, v));

        return new GpuSnapshot { Available = true, ByPid = byPid, TotalPct = total, TotalVramBytes = totalVram };
    }
}

/// <summary>
/// Samples GPU Engine / GPU Process Memory counters through one wildcard PDH query. Instance names change as
/// processes come and go, so the query is rebuilt every <see cref="RebuildInterval"/> in a double-buffered way
/// (the replacement is primed one sample ahead, so there is never a gap).
/// </summary>
internal sealed class GpuSampler : IGpuSource, IDisposable
{
    public const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    public const string DedicatedPath = @"\GPU Process Memory(*)\Dedicated Usage";
    public const string SharedPath = @"\GPU Process Memory(*)\Shared Usage";
    public static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private PdhQuery? _current;
    private PdhQuery? _next;
    private long _currentBuiltAt;
    private bool _nextPrimed;
    private volatile GpuSnapshot _latest = GpuSnapshot.Unavailable;
    private int _failures;
    private bool _disabled;

    public GpuSnapshot Latest => _latest;
    public bool IsAvailable => !_disabled && _latest.Available;

    public GpuSampler(ILogger log) => _log = log;

    private static PdhQuery? Build() => PdhQuery.TryCreate(EnginePath, DedicatedPath, SharedPath);

    /// <summary>Call every ~2 s.</summary>
    public void Sample()
    {
        if (_disabled) return;
        try
        {
            if (_current == null)
            {
                _current = Build();
                if (_current == null)
                {
                    if (++_failures >= 3) { _disabled = true; _log.LogInformation("GPU performance counters not available; GPU metrics disabled"); }
                    return;
                }
                _current.Collect(); // prime: rate counters need two samples
                _currentBuiltAt = Environment.TickCount64;
                return;
            }

            if (_next == null && Environment.TickCount64 - _currentBuiltAt > RebuildInterval.TotalMilliseconds)
            {
                _next = Build();
                _next?.Collect();
                _nextPrimed = false;
            }

            if (!_current.Collect()) return;
            _next?.Collect();

            var builder = new GpuSnapshotBuilder();
            bool ok = _current.ReadArray(0, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100,
                           (name, raw) => builder.AddEngine(name, Pdh.AsDouble(raw)))
                    & _current.ReadArray(1, Pdh.PDH_FMT_LARGE, (name, raw) => builder.AddDedicated(name, raw))
                    & _current.ReadArray(2, Pdh.PDH_FMT_LARGE, (name, raw) => builder.AddShared(name, raw));
            if (ok) _latest = builder.Build();

            if (_next != null)
            {
                if (_nextPrimed)
                {
                    _current.Dispose();
                    _current = _next;
                    _next = null;
                    _currentBuiltAt = Environment.TickCount64;
                }
                else _nextPrimed = true;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GPU sampling failed");
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _next?.Dispose();
    }
}
