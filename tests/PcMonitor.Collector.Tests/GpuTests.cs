using Microsoft.Extensions.Logging.Abstractions;
using PcMonitor.Collector.Sampling;
using Xunit;

namespace PcMonitor.Collector.Tests;

public class GpuTests
{
    // Unit tests for name parsing / aggregation plus one live check against the machine's real counters.
    [Fact]
    public void ParsesEngineInstanceNames()
    {
        Assert.True(GpuInstanceName.TryParse(
            "pid_1234_luid_0x00000000_0x0000F3E5_phys_0_eng_3_engtype_VideoDecode", out int pid, out var luid, out var type));
        Assert.Equal(1234, pid);
        Assert.Equal("0x00000000_0x0000F3E5", luid.ToString());
        Assert.Equal("VideoDecode", type.ToString());
    }

    [Fact]
    public void ParsesMemoryInstanceNames()
    {
        Assert.True(GpuInstanceName.TryParse("pid_77_luid_0x00000000_0x00011111_phys_0", out int pid, out var luid, out var type));
        Assert.Equal(77, pid);
        Assert.Equal("0x00000000_0x00011111", luid.ToString());
        Assert.True(type.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid__luid_x")]
    [InlineData("pid_abc_luid_x")]
    public void RejectsMalformedNames(string name)
        => Assert.False(GpuInstanceName.TryParse(name, out _, out _, out _));

    [Fact]
    public void BuilderTakesMaxAcrossEngineTypesAndSumsEnginesWithinAType()
    {
        var b = new GpuSnapshotBuilder();
        // pid 10: two 3D engines (30 + 25 = 55) and a Copy engine (10) -> 55
        b.AddEngine("pid_10_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 30);
        b.AddEngine("pid_10_luid_0x0_0xA_phys_0_eng_1_engtype_3D", 25);
        b.AddEngine("pid_10_luid_0x0_0xA_phys_0_eng_2_engtype_Copy", 10);
        // pid 20: video decode only
        b.AddEngine("pid_20_luid_0x0_0xA_phys_0_eng_5_engtype_VideoDecode", 12);
        // pid 30: on another adapter
        b.AddEngine("pid_30_luid_0x0_0xB_phys_0_eng_0_engtype_3D", 90);
        b.AddEngine("pid_40_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 0);   // zero ignored

        b.AddDedicated("pid_10_luid_0x0_0xA_phys_0", 500);
        b.AddDedicated("pid_10_luid_0x0_0xB_phys_0", 100);               // same pid, second adapter
        b.AddShared("pid_10_luid_0x0_0xA_phys_0", 40);
        b.AddDedicated("pid_20_luid_0x0_0xA_phys_0", 300);

        var s = b.Build();
        Assert.True(s.Available);
        Assert.Equal(55, s.ByPid[10].Pct);
        Assert.Equal(12, s.ByPid[20].Pct);
        Assert.Equal(90, s.ByPid[30].Pct);
        Assert.False(s.ByPid.ContainsKey(40));
        Assert.Equal(600, s.ByPid[10].VramDedicated);
        Assert.Equal(40, s.ByPid[10].VramShared);
        Assert.Equal(900, s.TotalVramBytes);
        // Adapter A 3D = 55 (+ pid 40 zero) ; adapter B 3D = 90 -> busiest engine of busiest adapter
        Assert.Equal(90, s.TotalPct);
    }

    [Fact]
    public void BuilderClampsToOneHundred()
    {
        var b = new GpuSnapshotBuilder();
        b.AddEngine("pid_1_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 80);
        b.AddEngine("pid_1_luid_0x0_0xA_phys_0_eng_1_engtype_3D", 80);
        var s = b.Build();
        Assert.Equal(100, s.ByPid[1].Pct);
        Assert.Equal(100, s.TotalPct);
    }

    [Fact]
    public void LivePdhCountersProduceSaneData()
    {
        using var sampler = new GpuSampler(NullLogger.Instance);
        sampler.Sample();
        Thread.Sleep(1100);
        sampler.Sample();
        var snap = sampler.Latest;
        using (var probe = PcMonitor.Collector.Native.PdhQuery.TryCreate(GpuSampler.EnginePath, GpuSampler.DedicatedPath, GpuSampler.SharedPath))
        {
            // Only machines without GPU performance counters (e.g. some VMs) may legitimately report "unavailable".
            if (probe == null) return;
        }
        Assert.True(snap.Available, "GPU counters exist but the sampler produced no snapshot");

        Assert.InRange(snap.TotalPct, 0, 100);
        foreach (var (pid, u) in snap.ByPid)
        {
            Assert.True(pid > 0);
            Assert.InRange(u.Pct, 0, 100);
            Assert.True(u.VramDedicated >= 0 && u.VramShared >= 0);
        }
    }
}
