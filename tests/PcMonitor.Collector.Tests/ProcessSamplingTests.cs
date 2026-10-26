using System.Diagnostics;
using PcMonitor.Collector.Sampling;
using Xunit;

namespace PcMonitor.Collector.Tests;

public class ProcessSamplingTests
{
    [Fact]
    public void ReaderParsesOwnProcessLikeTheFramework()
    {
        using var reader = new ProcessReader();
        Assert.True(reader.Snapshot());

        using var self = Process.GetCurrentProcess();
        bool found = false;
        int count = 0;
        foreach (var r in reader.Records())
        {
            count++;
            if (r.Pid != Environment.ProcessId) continue;
            found = true;

            string expectedName = self.ProcessName + ".exe";
            Assert.Equal(expectedName, r.NameString(), ignoreCase: true);
            Assert.Equal(self.Id, r.Pid);
            Assert.Equal(Process.GetProcessById(r.ParentPid).Id, r.ParentPid);

            // Memory figures must be in the same ballpark as the managed Process class.
            self.Refresh();
            Assert.InRange(r.WorkingSet, self.WorkingSet64 / 2, self.WorkingSet64 * 2);
            Assert.InRange(r.Commit, self.PrivateMemorySize64 / 2, self.PrivateMemorySize64 * 2);
            Assert.InRange(r.PrivateWorkingSet, 1, r.WorkingSet);
            Assert.InRange(r.Handles, self.HandleCount / 2, self.HandleCount * 2 + 50);
            Assert.InRange(r.Threads, 1, self.Threads.Count + 5);
            Assert.True(r.CreateTime > 0);
            Assert.True(r.CpuTime > 0);
        }

        Assert.True(found, "own process not present in snapshot");
        Assert.True(count > 20);
    }

    [Fact]
    public void TableComputesCpuForABusyProcess()
    {
        using var table = new ProcessTable();
        Assert.True(table.Update());

        // Burn roughly one core for ~1.2 s on a background thread, sampling around it.
        var stop = new CancellationTokenSource();
        var spin = new Thread(() => { while (!stop.IsCancellationRequested) Thread.SpinWait(1000); }) { IsBackground = true };
        spin.Start();
        Thread.Sleep(1200);
        Assert.True(table.Update());
        stop.Cancel();
        spin.Join();

        Assert.True(table.TryGet(Environment.ProcessId, out var me));
        double expected = 100.0 / table.LogicalCores;
        // One saturated thread should be around 1/N of the machine; allow generous noise.
        Assert.InRange(me.CpuPct, expected * 0.4, Math.Min(100, expected * 2.5 + 5));
        Assert.True(me.PrivateBytes > 1_000_000);
        Assert.False(string.IsNullOrEmpty(me.Path));
        Assert.Equal(PcMonitor.Core.AppKey.FromPath(me.Path!), me.AppId);
    }

    [Fact]
    public void DeadProcessesStillResolveBriefly()
    {
        using var table = new ProcessTable();
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false };
        using var child = Process.Start(psi)!;
        Thread.Sleep(300);
        Assert.True(table.Update());
        Assert.True(table.TryGet(child.Id, out var entry));
        long appId = entry.AppId;

        child.Kill(true);
        child.WaitForExit();
        Thread.Sleep(300);
        Assert.True(table.Update());

        Assert.False(table.TryGet(child.Id, out _));
        Assert.True(table.TryGetAppId(child.Id, out long resolved));
        Assert.Equal(appId, resolved);
    }

    [Theory]
    [InlineData(0, 1e7, 0)]
    [InlineData(5_000_000, 1e7, 50)]
    [InlineData(40_000_000, 1e7, 100)]
    [InlineData(-5, 1e7, 0)]
    public void CpuPercentIsClamped(long delta, double capacity, double expected)
        => Assert.Equal(expected, CpuCalculator.Percent(delta, capacity), 6);

    [Fact]
    public void SystemSamplerReportsSaneMemoryAndCpu()
    {
        var s = new SystemSampler();
        s.Sample();
        Thread.Sleep(300);
        s.Sample();
        Assert.InRange(s.CpuPct, 0, 100);
        Assert.True(s.RamTotalBytes > 1L << 30);
        Assert.InRange(s.RamUsedBytes, 1, s.RamTotalBytes);
    }
}
