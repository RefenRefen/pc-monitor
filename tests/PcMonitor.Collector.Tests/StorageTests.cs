using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Storage;
using PcMonitor.Core;
using Xunit;

namespace PcMonitor.Collector.Tests;

internal static class TestData
{
    public static TickResult Tick(long ts, params AppTick[] apps) => new()
    {
        Ts = ts,
        Seconds = 1,
        System = new SystemTotals
        {
            CpuPct = apps.Sum(a => a.Cpu), RamTotalBytes = 16L << 30, RamUsedBytes = 8L << 30,
            GpuPct = apps.Sum(a => a.Gpu), NetRxBps = apps.Sum(a => a.NetRx), NetTxBps = apps.Sum(a => a.NetTx),
            DiskReadBps = apps.Sum(a => a.DiskRead), DiskWriteBps = apps.Sum(a => a.DiskWrite),
        },
        Apps = apps,
    };

    public static AppTick App(long id, double cpu = 0, long ram = 10_000_000, double gpu = 0, long rx = 0, long tx = 0, long dr = 0, long dw = 0) =>
        new() { AppId = id, Cpu = cpu, Private = ram, Commit = ram * 2, Gpu = gpu, NetRx = rx, NetTx = tx, DiskRead = dr, DiskWrite = dw, Processes = 1 };

    public static string TempDb() => Path.Combine(Path.GetTempPath(), "pcmon-test-" + Guid.NewGuid().ToString("N") + ".db");

    public static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(f); } catch { /* ignore */ }
    }
}

public class TickRingTests
{
    [Fact]
    public void KeepsOnlyTheLastNInChronologicalOrder()
    {
        var ring = new TickRing(5);
        for (int i = 1; i <= 8; i++)
            ring.Append(new TickRecord(i, 1, new SystemTotals(), Array.Empty<AppTick>()));

        var snap = ring.Snapshot();
        Assert.Equal(new long[] { 4, 5, 6, 7, 8 }, snap.Select(r => r.Ts).ToArray());
        Assert.Equal(8, ring.Latest()!.Ts);
        Assert.Equal(new long[] { 6, 7 }, ring.Snapshot(6, 7).Select(r => r.Ts).ToArray());
    }

    [Fact]
    public void SummarizeAveragesOverTheWholeWindow()
    {
        var ring = new TickRing(10);
        for (int i = 0; i < 10; i++)
            ring.Append(new TickRecord(100 + i, 1, new SystemTotals(),
                new[] { TestData.App(1, cpu: i < 5 ? 40 : 0, rx: 1000) }));

        var s = TickRing.Summarize(ring.Snapshot(), id => ("app", "C:\\app.exe")).Single();
        Assert.Equal(20, s.AvgCpu, 6);         // 40% for half the window
        Assert.Equal(40, s.MaxCpu);
        Assert.Equal(10_000, s.NetRxBytes);
        Assert.Equal("app", s.Name);
    }

    [Fact]
    public void SeriesForAnAppIsZeroWhereItWasAbsent()
    {
        var ring = new TickRing(4);
        ring.Append(new TickRecord(1, 1, new SystemTotals(), new[] { TestData.App(7, cpu: 10) }));
        ring.Append(new TickRecord(2, 1, new SystemTotals(), Array.Empty<AppTick>()));
        var pts = TickRing.ToAppSeries(ring.Snapshot(), 7);
        Assert.Equal(2, pts.Count);
        Assert.Equal(10, pts[0].Cpu);
        Assert.Equal(0, pts[1].Cpu);
    }
}

public class MinuteAccumulatorTests
{
    [Fact]
    public void ProducesOneBatchPerMinuteWithOnlySignificantApps()
    {
        var acc = new MinuteAccumulator();
        MinuteBatch? first = null;
        long start = 6000; // multiple of 60
        for (int i = 0; i < 125; i++)
        {
            var b = acc.Add(TestData.Tick(start + i,
                TestData.App(1, cpu: 50, ram: 200_000_000, rx: 1000),    // significant
                TestData.App(2, cpu: 0, ram: 1_000_000)));                // idle and small -> dropped
            if (b != null && first == null) first = b;
        }

        Assert.NotNull(first);
        Assert.Equal(start, first!.Ts);
        Assert.Equal(60, first.Duration, 6);
        var row = Assert.Single(first.Apps);
        Assert.Equal(1, row.AppId);
        Assert.Equal(50, row.CpuAvg, 6);
        Assert.Equal(200_000_000, row.RamAvg, 3);
        Assert.Equal(60_000, row.NetRx);                                  // 1000 B/s * 60 s
        Assert.Equal(50, first.System.CpuAvg, 6);   // test system CPU = sum of the apps' CPU
    }

    [Fact]
    public void AbsentSecondsCountAsZeroCpuButNotZeroRam()
    {
        var acc = new MinuteAccumulator();
        long start = 12000;
        for (int i = 0; i < 60; i++)
        {
            // App is only present for the first 30 seconds of the minute.
            var apps = i < 30 ? new[] { TestData.App(5, cpu: 80, ram: 300_000_000) } : Array.Empty<AppTick>();
            acc.Add(TestData.Tick(start + i, apps));
        }
        var batch = acc.Flush()!;
        var row = Assert.Single(batch.Apps);
        Assert.Equal(40, row.CpuAvg, 6);                  // 80% * 30/60
        Assert.Equal(300_000_000, row.RamAvg, 3);         // averaged while alive
    }

    [Fact]
    public void FlushOnEmptyAccumulatorReturnsNull() => Assert.Null(new MinuteAccumulator().Flush());
}

public class UsageDatabaseTests : IDisposable
{
    private readonly string _path = TestData.TempDb();
    private readonly UsageDatabase _db;

    public UsageDatabaseTests() => _db = new UsageDatabase(_path);

    public void Dispose()
    {
        _db.Dispose();
        TestData.Cleanup(_path);
    }

    private static MinuteBatch Batch(long ts, long appId, double cpu, double ram = 100e6, long rxBytes = 6000, double gpu = 0)
    {
        var b = new MinuteBatch { Ts = ts, Duration = 60 };
        b.System = new SystemMinuteRow { CpuAvg = cpu, CpuMax = cpu * 2, RamUsedAvg = 8e9, RamTotal = 16e9, NetRx = rxBytes, GpuAvg = gpu, GpuMax = gpu };
        b.Apps.Add(new AppMinuteRow { AppId = appId, CpuAvg = cpu, CpuMax = cpu * 2, RamAvg = ram, RamMax = ram * 1.5, CommitMax = ram * 2, GpuAvg = gpu, GpuMax = gpu, NetRx = rxBytes });
        return b;
    }

    [Fact]
    public void SummaryAveragesOverCoveredTimeAndSumsBytes()
    {
        long t0 = 1_000_000 / 60 * 60;
        // app 1: 10% for 10 minutes; app 2 only present in 2 of them at 50%.
        for (int i = 0; i < 10; i++)
        {
            var b = Batch(t0 + i * 60, 1, 10);
            if (i < 2) b.Apps.Add(new AppMinuteRow { AppId = 2, CpuAvg = 50, CpuMax = 90, RamAvg = 5e8, RamMax = 6e8 });
            _db.WriteMinute(b);
        }

        var sum = _db.GetSummary(t0, t0 + 600).ToDictionary(s => s.AppId);
        Assert.Equal(10, sum[1].AvgCpu, 6);
        Assert.Equal(20, sum[1].MaxCpu, 6);
        Assert.Equal(60_000, sum[1].NetRxBytes);
        // 50% * 120 s over 600 s covered = 10%
        Assert.Equal(10, sum[2].AvgCpu, 6);
        Assert.Equal(90, sum[2].MaxCpu, 6);
        Assert.Equal(5e8, sum[2].AvgRam, 3);

        // A narrower window only sees what it covers.
        var narrow = _db.GetSummary(t0, t0 + 120).ToDictionary(s => s.AppId);
        Assert.Equal(50, narrow[2].AvgCpu, 6);
    }

    [Fact]
    public void SeriesForSystemAndForApp()
    {
        long t0 = 2_000_000 / 360 * 360;   // aligned so the six minutes fall into one 360 s bucket
        for (int i = 0; i < 6; i++) _db.WriteMinute(Batch(t0 + i * 60, 1, cpu: 10 * (i + 1), rxBytes: 60 * 100));

        var minute = _db.GetSeries(t0, t0 + 360, 60, null);
        Assert.Equal(6, minute.Count);
        Assert.Equal(10, minute[0].Cpu, 6);
        Assert.Equal(60, minute[5].Cpu, 6);
        Assert.Equal(100, minute[0].NetRx, 6);          // 6000 bytes over 60 s = 100 B/s

        var coarse = _db.GetSeries(t0, t0 + 360, 360, null);
        Assert.Single(coarse);
        Assert.Equal(35, coarse[0].Cpu, 6);             // mean of 10..60
        Assert.Equal(120, coarse[0].CpuMax, 6);

        var app = _db.GetSeries(t0, t0 + 360, 60, 1);
        Assert.Equal(6, app.Count);
        Assert.Empty(_db.GetSeries(t0, t0 + 360, 60, 999));
    }

    [Fact]
    public void RetentionRollsOldMinutesIntoQuarterHoursWithoutLosingTotals()
    {
        long now = 5_000_000;
        long cutAligned = (now - UsageDatabase.MinuteRetentionDays * 86400L) / 900 * 900;
        long oldStart = (cutAligned - 3600) / 900 * 900;     // one hour of old data = 4 quarters
        for (int i = 0; i < 60; i++) _db.WriteMinute(Batch(oldStart + i * 60, 1, cpu: 20, ram: 400e6, rxBytes: 1200, gpu: 10));
        // recent data stays at minute resolution
        long recent = now / 60 * 60 - 600;
        for (int i = 0; i < 5; i++) _db.WriteMinute(Batch(recent + i * 60, 1, cpu: 5));

        var before = _db.GetSummary(oldStart, oldStart + 3600).Single();
        var res = _db.RunRetention(now);

        Assert.Equal(4, res.MinuteRowsRolledUp);
        Assert.Equal(5, _db.CountRows("minute_usage"));
        Assert.Equal(4, _db.CountRows("quarter_usage"));
        Assert.Equal(4, _db.CountRows("system_quarter"));

        var after = _db.GetSummary(oldStart, oldStart + 3600).Single();
        Assert.Equal(before.AvgCpu, after.AvgCpu, 6);
        Assert.Equal(before.AvgRam, after.AvgRam, 3);
        Assert.Equal(before.AvgGpu, after.AvgGpu, 6);
        Assert.Equal(before.MaxCpu, after.MaxCpu, 6);
        Assert.Equal(before.NetRxBytes, after.NetRxBytes);
        Assert.Equal(60 * 1200, after.NetRxBytes);

        // Series spanning both resolutions is continuous and rate-correct.
        var series = _db.GetSeries(oldStart, now, 900, null);
        Assert.Equal(20, series.First().Cpu, 6);
        Assert.Equal(1200.0 / 60, series.First().NetRx, 6);

        // Running it again changes nothing.
        var again = _db.RunRetention(now);
        Assert.Equal(0, again.MinuteRowsRolledUp);
        Assert.Equal(4, _db.CountRows("quarter_usage"));
    }

    [Fact]
    public void RetentionDropsQuarterRowsAfterNinetyDays()
    {
        long now = 50_000_000;
        long ancient = (now - 120 * 86400L) / 900 * 900;
        _db.WriteMinute(Batch(ancient, 1, 10));
        _db.RunRetention(now);   // rolls to quarter...
        _db.RunRetention(now);   // ...which is older than 90 days, so it is dropped
        Assert.Equal(0, _db.CountRows("minute_usage"));
        Assert.Equal(0, _db.CountRows("quarter_usage"));
    }

    [Fact]
    public void ConnectionsAccumulateAcrossFlushes()
    {
        _db.UpsertConnections(new[] { new ConnectionRow(1, "1.2.3.4", 443, "tcp", 100, 1000, 1000) });
        _db.UpsertConnections(new[]
        {
            new ConnectionRow(1, "1.2.3.4", 443, "tcp", 50, 1060, 1060),
            new ConnectionRow(1, "1.2.3.4", 53, "udp", 10, 1060, 1060),
        });
        var list = _db.GetConnections(1, 10);
        Assert.Equal(2, list.Count);
        var tcp = list.Single(c => c.Proto == "tcp");
        Assert.Equal(150, tcp.Bytes);
        Assert.Equal(1000, tcp.FirstSeenUnix);
        Assert.Equal(1060, tcp.LastSeenUnix);
        Assert.Empty(_db.GetConnections(2, 10));
    }

    [Fact]
    public void IdentityRoundTripsThroughTheAppsTable()
    {
        var id = new AppIdentity
        {
            AppId = 42, ExePath = @"C:\x\app.exe", Name = "My App", Company = "ACME", Version = "1.2.3",
            Signature = SignatureState.Signed, Signer = "ACME Ltd", Sha256 = "abc",
            Startup = { new StartupEntry { Kind = StartupKind.RunKey, Name = "MyApp", Command = "app.exe /min", CanDisable = true } },
            FirstSeenUnix = 10, LastSeenUnix = 20, Complete = true,
        };
        _db.UpsertApp(id, fileMtimeUnix: 555);

        var loaded = _db.LoadIdentity(42)!;
        Assert.Equal("My App", loaded.Identity.Name);
        Assert.Equal("ACME Ltd", loaded.Identity.Signer);
        Assert.Equal(SignatureState.Signed, loaded.Identity.Signature);
        Assert.Equal("app.exe /min", loaded.Identity.Startup.Single().Command);
        Assert.Equal(555, loaded.FileMtimeUnix);
        Assert.Null(_db.LoadIdentity(43));

        // Names surface in summaries.
        _db.WriteMinute(Batch(60_000, 42, 5));
        Assert.Equal("My App", _db.GetSummary(0, 100_000).Single().Name);
        Assert.Equal("ACME", _db.GetSummary(0, 100_000).Single().Publisher);
    }

    [Fact]
    public void MetaKeyValueStore()
    {
        Assert.Null(_db.GetMeta("k"));
        _db.SetMeta("k", "1");
        _db.SetMeta("k", "2");
        Assert.Equal("2", _db.GetMeta("k"));
    }
}
