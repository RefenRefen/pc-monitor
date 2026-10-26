using PcMonitor.Core;
using Xunit;

namespace PcMonitor.Core.Tests;

public class ImpactScoreTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void IdleAppScoresZero()
    {
        Assert.Equal(0, ImpactScore.Compute(0, 0, 16 * Gb, 0, 0, 0));
    }

    [Fact]
    public void ScoreIsBoundedAndMonotonic()
    {
        double low = ImpactScore.Compute(5, Gb / 4, 16 * Gb, 0, 1000, 1000);
        double mid = ImpactScore.Compute(25, Gb, 16 * Gb, 10, 1_000_000, 5_000_000);
        double max = ImpactScore.Compute(100, 16 * Gb, 16 * Gb, 100, 1L << 40, 1L << 40);
        Assert.InRange(low, 0, 100);
        Assert.True(low < mid);
        Assert.True(mid < max);
        Assert.InRange(max, 99, 100);
    }

    [Fact]
    public void WeightsSumToOne()
    {
        double sum = ImpactScore.WeightCpu + ImpactScore.WeightGpu + ImpactScore.WeightRam
                   + ImpactScore.WeightDisk + ImpactScore.WeightNet;
        Assert.Equal(1.0, sum, 9);
    }

    [Fact]
    public void CpuIsWeightedHigherThanNetwork()
    {
        double cpu = ImpactScore.Compute(ImpactScore.HeavyCpuPct, 0, 16 * Gb, 0, 0, 0);
        double net = ImpactScore.Compute(0, 0, 16 * Gb, 0, (long)ImpactScore.HeavyNetBytesPerSec, 0);
        Assert.True(cpu > net);
    }
}
