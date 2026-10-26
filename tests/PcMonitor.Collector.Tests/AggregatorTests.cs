using System.Net;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Sampling;
using PcMonitor.Core;
using Xunit;

namespace PcMonitor.Collector.Tests;

internal sealed class FakeGpu : IGpuSource
{
    public GpuSnapshot Latest { get; set; } = GpuSnapshot.Unavailable;
}

internal sealed class FakeMeta : IAppMetadata
{
    public readonly List<long> Observed = new();
    public void Observe(long appId, string? exePath, string processName) => Observed.Add(appId);
    public string? GetDisplayName(long appId) => null;
    public AppFlags GetStaticFlags(long appId) => AppFlags.None;
}

public class AggregatorTests
{
    private static (AppAggregator agg, ProcessTable table, EtwCounters etw, FakeGpu gpu, FakeMeta meta, UserContext user) Create(bool etwOn = true)
    {
        var table = new ProcessTable();
        var etw = new EtwCounters();
        var gpu = new FakeGpu();
        var meta = new FakeMeta();
        var user = new UserContext();
        var sys = new SystemSampler();
        sys.Sample();
        var agg = new AppAggregator(table, sys, etw, () => etwOn, gpu, meta, user);
        return (agg, table, etw, gpu, meta, user);
    }

    [Fact]
    public void GroupsProcessesByAppAndMergesEtwAndGpu()
    {
        var (agg, table, etw, gpu, meta, _) = Create();
        using var _t = table;
        table.Update();
        agg.Tick();                       // prime
        Thread.Sleep(1000);
        table.Update();

        int me = Environment.ProcessId;
        table.TryGet(me, out var entry);
        long appId = entry!.AppId;

        etw.AddNet(me, send: true, 5000, IpKey.From(IPAddress.Parse("93.184.216.34")), 443, udp: false);
        etw.AddNet(me, send: false, 20000, IpKey.From(IPAddress.Parse("93.184.216.34")), 443, udp: false);
        etw.AddDisk(me, write: true, 4096);
        etw.AddDisk(me, write: false, 8192);
        gpu.Latest = new GpuSnapshot
        {
            Available = true,
            TotalPct = 12,
            TotalVramBytes = 1000,
            ByPid = new() { [me] = new GpuPidUsage(7.5, 1000, 200) },
        };

        var r = agg.Tick();

        var tick = r.Apps.Single(a => a.AppId == appId);
        double dt = r.Seconds;
        Assert.Equal(5000 / dt, tick.NetTx, 1.0);
        Assert.Equal(20000 / dt, tick.NetRx, 1.0);
        Assert.Equal(4096 / dt, tick.DiskWrite, 1.0);
        Assert.Equal(8192 / dt, tick.DiskRead, 1.0);
        Assert.Equal(7.5, tick.Gpu);
        Assert.Equal(1000, tick.VramDedicated);
        Assert.True(tick.Private > 1_000_000);
        Assert.InRange(tick.Impact, 0, 100);
        Assert.Contains(appId, meta.Observed);

        Assert.True(r.EtwAvailable);
        Assert.True(r.GpuAvailable);
        Assert.Equal(12, r.System.GpuPct);
        Assert.InRange(r.System.NetRxBps + r.System.NetTxBps, (long)(25000 / dt) - 2, (long)(25000 / dt) + 2);
        Assert.Single(r.Connections);
        Assert.Equal(1, agg.Connections.DistinctHosts(appId));
        Assert.True(r.System.RamTotalBytes > r.System.RamUsedBytes);
        Assert.Equal(table.Entries.Count, r.System.ProcessCount);
    }

    [Fact]
    public void MultipleProcessesOfOneExecutableShareOneApp()
    {
        var (agg, table, _, _, _, _) = Create();
        using var _t = table;
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false };
        using var c1 = System.Diagnostics.Process.Start(psi)!;
        using var c2 = System.Diagnostics.Process.Start(psi)!;
        try
        {
            Thread.Sleep(400);
            table.Update();
            var r = agg.Tick();
            table.TryGet(c1.Id, out var e1);
            table.TryGet(c2.Id, out var e2);
            Assert.Equal(e1!.AppId, e2!.AppId);
            var app = r.Apps.Single(a => a.AppId == e1.AppId);
            Assert.True(app.Processes >= 2);
        }
        finally { c1.Kill(true); c2.Kill(true); }
    }

    [Fact]
    public void WhenEtwIsOffDiskFallsBackToProcessCountersAndNetworkIsZero()
    {
        var (agg, table, etw, _, _, _) = Create(etwOn: false);
        using var _t = table;
        table.Update();
        agg.Tick();
        var buf = new byte[8 * 1024 * 1024];
        string tmp = Path.GetTempFileName();
        try
        {
            Thread.Sleep(500);
            File.WriteAllBytes(tmp, buf);
            Thread.Sleep(500);
            table.Update();
            var r = agg.Tick();
            Assert.False(r.EtwAvailable);
            Assert.Equal(0, r.System.NetRxBps);
            Assert.True(r.System.DiskWriteBps > 1_000_000, $"disk write fallback was {r.System.DiskWriteBps}");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void WindowContextMarksAppsWithVisibleWindows()
    {
        var (agg, table, _, _, _, user) = Create();
        using var _t = table;
        table.Update();
        agg.Tick();
        // Unknown until the widget reports.
        Assert.Null(user.Current);
        user.Report(new[] { Environment.ProcessId }, 5);
        Assert.NotNull(user.Current);
        table.Update();
        var r = agg.Tick();
        Assert.NotEmpty(r.Apps);
    }
}

public class EtwCountersTests
{
    [Fact]
    public void DrainReturnsDeltasAndResets()
    {
        var c = new EtwCounters();
        var ip = IpKey.From(IPAddress.Parse("10.1.2.3"));
        c.AddNet(100, true, 10, ip, 80, false);
        c.AddNet(100, true, 15, ip, 80, false);
        c.AddNet(100, false, 7, ip, 80, false);
        c.AddNet(200, false, 3, IpKey.From(IPAddress.Parse("2001:db8::1")), 53, true);
        c.AddDisk(100, true, 512);
        c.AddDisk(0, false, 64); // unattributed

        var pids = new List<PidDelta>();
        var conns = new List<ConnDelta>();
        c.Drain(pids, conns, out long rx, out long tx, out long dr, out long dw);

        Assert.Equal(10, rx);
        Assert.Equal(25, tx);
        Assert.Equal(64, dr);
        Assert.Equal(512, dw);
        var p100 = pids.Single(p => p.Pid == 100);
        Assert.Equal(7, p100.Rx);
        Assert.Equal(25, p100.Tx);
        Assert.Equal(512, p100.DiskWrite);
        Assert.Equal(2, conns.Count);
        Assert.Equal(32, conns.Single(x => x.Key.Pid == 100).Bytes);

        c.Drain(pids, conns, out rx, out tx, out dr, out dw);
        Assert.Empty(pids);
        Assert.Empty(conns);
        Assert.Equal(0, rx + tx + dr + dw);
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("::1")]
    [InlineData("2606:4700:4700::1111")]
    public void IpKeyRoundTrips(string ip)
    {
        var a = IPAddress.Parse(ip);
        Assert.Equal(a, IpKey.From(a).ToAddress());
    }

    [Fact]
    public void LocalAddressSetPicksTheRemotePeer()
    {
        var set = new LocalAddressSet();
        set.Refresh();
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        var remote = IPAddress.Parse("8.8.8.8");
        if (local == null) return; // no network adapter on this machine

        // Send: destination is remote.
        Assert.True(set.TryGetRemote(remote, 443, local, 50000, out var r1, out int p1));
        Assert.Equal(remote, r1); Assert.Equal(443, p1);
        // Receive with swapped view: destination is local.
        Assert.True(set.TryGetRemote(local, 50000, remote, 443, out var r2, out int p2));
        Assert.Equal(remote, r2); Assert.Equal(443, p2);
        // Loopback and local-to-local are not network traffic.
        Assert.False(set.TryGetRemote(IPAddress.Loopback, 1, IPAddress.Loopback, 2, out _, out _));
        Assert.False(set.TryGetRemote(local, 1, local, 2, out _, out _));
    }

    [Fact]
    public void EtwSessionFailsGracefullyWithoutAdminRights()
    {
        bool isAdmin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        using var mon = new EtwMonitor(new EtwCounters(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        bool started = mon.TryStart();
        if (isAdmin) Assert.True(started, mon.FailureReason);
        else
        {
            Assert.False(started);
            Assert.False(mon.IsRunning);
            Assert.False(string.IsNullOrEmpty(mon.FailureReason));
        }
    }
}
