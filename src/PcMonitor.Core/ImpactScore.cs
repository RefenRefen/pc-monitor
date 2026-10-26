namespace PcMonitor.Core;

/// <summary>
/// Weighted, normalised "how much is this app hurting my machine" score in 0..100.
/// Every resource is mapped through a saturating curve 1 - exp(-x / heavy) where
/// <c>heavy</c> is a usage level that we consider heavy, so the score is not dominated
/// by whichever resource happens to have the biggest raw numbers (bytes vs percent).
/// </summary>
public static class ImpactScore
{
    // Weights sum to 1.
    public const double WeightCpu = 0.30;
    public const double WeightGpu = 0.20;
    public const double WeightRam = 0.20;
    public const double WeightDisk = 0.15;
    public const double WeightNet = 0.15;

    // "Heavy" reference levels (score contribution reaches ~63% of its weight here).
    public const double HeavyCpuPct = 25.0;
    public const double HeavyGpuPct = 25.0;
    public const double HeavyRamFraction = 0.10;          // of total RAM
    public const double HeavyDiskBytesPerSec = 20.0 * 1024 * 1024;
    public const double HeavyNetBytesPerSec = 5.0 * 1024 * 1024;

    public static double Compute(
        double cpuPct, long privateBytes, long totalRamBytes,
        double gpuPct, long netBytesPerSec, long diskBytesPerSec)
    {
        double ramFrac = totalRamBytes > 0 ? (double)privateBytes / totalRamBytes : 0;
        double s =
            WeightCpu * Saturate(cpuPct / HeavyCpuPct) +
            WeightGpu * Saturate(gpuPct / HeavyGpuPct) +
            WeightRam * Saturate(ramFrac / HeavyRamFraction) +
            WeightDisk * Saturate(diskBytesPerSec / HeavyDiskBytesPerSec) +
            WeightNet * Saturate(netBytesPerSec / HeavyNetBytesPerSec);
        return Math.Clamp(s * 100.0, 0, 100);
    }

    private static double Saturate(double x) => x <= 0 ? 0 : 1.0 - Math.Exp(-x);
}
