namespace PcMonitor.Collector.Sampling;

internal readonly record struct GpuPidUsage(double Pct, long VramDedicated, long VramShared);

/// <summary>Result of one GPU sampling pass.</summary>
internal sealed class GpuSnapshot
{
    public static readonly GpuSnapshot Unavailable = new() { Available = false };

    public bool Available { get; init; }
    public Dictionary<int, GpuPidUsage> ByPid { get; init; } = new();
    /// <summary>Busiest engine type of the busiest adapter, like Task Manager's GPU column.</summary>
    public double TotalPct { get; init; }
    public long TotalVramBytes { get; init; }
}

internal interface IGpuSource
{
    GpuSnapshot Latest { get; }
}
