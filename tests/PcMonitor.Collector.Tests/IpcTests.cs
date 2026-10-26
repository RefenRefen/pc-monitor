using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using PcMonitor.Collector;
using PcMonitor.Collector.Ipc;
using PcMonitor.Core;
using Xunit;

namespace PcMonitor.Collector.Tests;

/// <summary>Runs a real engine + pipe server (no ETW) and talks to it through a real named pipe.</summary>
public sealed class IpcFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmon-ipc-" + Guid.NewGuid().ToString("N"));
    internal CollectorEngine Engine { get; }
    internal PipeServer Server { get; }
    public string PipeName { get; } = "PcMonitor.Test." + Guid.NewGuid().ToString("N");

    public IpcFixture()
    {
        Engine = new CollectorEngine(Path.Combine(_dir, "usage.db"), NullLogger.Instance, startEtw: false, autoStartIdentity: true);
        Server = new PipeServer(Engine.Context, PipeName);
        Server.Start();
        Engine.Tick();
        Thread.Sleep(1100);
        Engine.Tick();
    }

    public async Task<NamedPipeClientStream> ConnectAsync()
    {
        var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(5000);
        return c;
    }

    public async Task<T> CallAsync<T>(PipeMessage request) where T : PipeMessage
    {
        await using var c = await ConnectAsync();
        request.Id = 7;
        await PipeFraming.WriteAsync(c, request, CancellationToken.None);
        var response = await PipeFraming.ReadAsync(c, new CancellationTokenSource(5000).Token);
        Assert.NotNull(response);
        Assert.Equal(7, response!.Id);
        return Assert.IsType<T>(response);
    }

    public void Dispose()
    {
        Server.Dispose();
        Engine.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* sqlite may still hold a handle briefly */ }
    }
}

public class IpcTests : IClassFixture<IpcFixture>
{
    private readonly IpcFixture _f;
    public IpcTests(IpcFixture f) => _f = f;

    [Fact]
    public async Task HelloDescribesTheMachine()
    {
        var hello = await _f.CallAsync<HelloResponse>(new HelloRequest());
        Assert.Equal(Environment.ProcessorCount, hello.LogicalCores);
        Assert.True(hello.RamTotalBytes > 1L << 30);
        Assert.False(hello.EtwAvailable);
    }

    [Fact]
    public async Task SeveralClientsCanBeConnectedAtTheSameTime()
    {
        // Regression: creating the 2nd pipe instance failed with "access denied" for a non-SYSTEM server account.
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < 4; i++) clients.Add(await _f.ConnectAsync());
            foreach (var c in clients)
            {
                await PipeFraming.WriteAsync(c, new HelloRequest { Id = 3 }, CancellationToken.None);
                var r = await PipeFraming.ReadAsync(c, new CancellationTokenSource(5000).Token);
                Assert.IsType<HelloResponse>(r);
            }
        }
        finally { foreach (var c in clients) await c.DisposeAsync(); }
    }

    [Fact]
    public async Task LiveHistoryAndSummaryServeRecentData()
    {
        _f.Engine.Tick();
        var hist = await _f.CallAsync<LiveHistoryResponse>(new LiveHistoryRequest());
        Assert.NotEmpty(hist.Points);

        long now = TimeUtil.NowUnix();
        var sum = await _f.CallAsync<SummaryResponse>(new SummaryRequest { FromUnix = now - 300, ToUnix = now + 5 });
        Assert.NotEmpty(sum.Apps);
        Assert.All(sum.Apps, a => Assert.False(string.IsNullOrEmpty(a.Name)));
    }

    [Fact]
    public async Task SubscribeStreamsFramesAndUnsubscribesOnDisconnect()
    {
        await using (var c = await _f.ConnectAsync())
        {
            await PipeFraming.WriteAsync(c, new SubscribeRequest { Id = 1, Detail = LiveDetail.Compact, TopN = 3 }, CancellationToken.None);
            var ack = await PipeFraming.ReadAsync(c, new CancellationTokenSource(5000).Token);
            Assert.IsType<OkResponse>(ack);

            var cts = new CancellationTokenSource(5000);
            var first = Assert.IsType<LiveFrame>(await PipeFraming.ReadAsync(c, cts.Token)); // replayed latest tick
            Assert.True(first.Apps.Count <= 3);

            _f.Engine.Tick();
            var next = Assert.IsType<LiveFrame>(await PipeFraming.ReadAsync(c, cts.Token));
            Assert.True(next.Ts >= first.Ts);
            Assert.Equal(1, _f.Engine.Context.Broadcaster.Count);
        }

        for (int i = 0; i < 50 && _f.Engine.Context.Broadcaster.Count != 0; i++) await Task.Delay(50);
        Assert.Equal(0, _f.Engine.Context.Broadcaster.Count);
    }

    [Fact]
    public async Task ReportContextIsAccepted()
    {
        var r = await _f.CallAsync<OkResponse>(new ReportContextRequest { VisibleWindowPids = { Environment.ProcessId }, IdleSeconds = 12 });
        Assert.NotNull(r);
        var snap = _f.Engine.Context.User.Current;
        Assert.NotNull(snap);
        Assert.Contains(Environment.ProcessId, snap!.VisibleWindowPids);
        Assert.Equal(12, snap.IdleSeconds);
    }

    [Fact]
    public async Task EndProcessTerminatesAChildProcessWithTheCallersRights()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                _f.Engine.Tick();
                lock (_f.Engine.Context.Table.SyncRoot) if (_f.Engine.Context.Table.TryGet(child.Id, out _)) break;
                await Task.Delay(100);
            }
            var resp = await _f.CallAsync<CommandResponse>(new CommandRequest { Command = CommandKind.EndProcess, Pid = child.Id });
            Assert.True(resp.Success, resp.Message);
            Assert.True(child.WaitForExit(5000));
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task ProtectedProcessesCannotBeEnded(int pid)
    {
        var resp = await _f.CallAsync<CommandResponse>(new CommandRequest { Command = CommandKind.EndProcess, Pid = pid });
        Assert.False(resp.Success);
    }

    [Fact]
    public async Task CollectorNeverEndsItself()
    {
        var resp = await _f.CallAsync<CommandResponse>(new CommandRequest { Command = CommandKind.EndProcess, Pid = Environment.ProcessId });
        Assert.False(resp.Success);
    }

    [Fact]
    public async Task StartupCommandRejectsUnknownEntries()
    {
        var resp = await _f.CallAsync<CommandResponse>(new CommandRequest
        {
            Command = CommandKind.DisableStartup,
            AppId = 12345,
            Startup = new StartupEntry { Name = "x", Location = "HKCU Run", Kind = StartupKind.RunKey, ApprovedKey = @"HKLM\SOFTWARE\Evil" },
        });
        Assert.False(resp.Success);
    }

    [Fact]
    public async Task StartupEntryCanBeDisabledAndReenabledThroughStartupApproved()
    {
        string exe = Environment.ProcessPath!;
        long appId = AppKey.FromPath(exe);
        string valueName = "PcMonitorTest_" + Guid.NewGuid().ToString("N");
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey))
            run.SetValue(valueName, "\"" + exe + "\" --pcmonitor-test");
        try
        {
            var ctx = _f.Engine.Context;
            for (int i = 0; i < 100 && ctx.Identity.GetIdentity(appId)?.Complete != true; i++)
            {
                _f.Engine.Tick();
                await Task.Delay(50);
            }
            var identity = ctx.Identity.GetIdentity(appId);
            Assert.NotNull(identity);
            ctx.Identity.InvalidateStartupIndex();
            ctx.Identity.RefreshStartup(identity!);
            var entry = identity!.Startup.FirstOrDefault(e => e.Name == valueName);
            Assert.NotNull(entry);
            Assert.True(entry!.CanDisable);

            var off = await _f.CallAsync<CommandResponse>(new CommandRequest { Command = CommandKind.DisableStartup, AppId = appId, Startup = entry });
            Assert.True(off.Success, off.Message);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey))
            {
                var bytes = (byte[])key!.GetValue(valueName)!;
                Assert.Equal(12, bytes.Length);
                Assert.Equal(0x03, bytes[0]);
            }

            ctx.Identity.RefreshStartup(identity);
            var after = identity.Startup.First(e => e.Name == valueName);
            Assert.False(after.Enabled);

            var on = await _f.CallAsync<CommandResponse>(new CommandRequest { Command = CommandKind.EnableStartup, AppId = appId, Startup = after });
            Assert.True(on.Success, on.Message);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey))
                Assert.Equal(0x02, ((byte[])key!.GetValue(valueName)!)[0]);
        }
        finally
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true)) run?.DeleteValue(valueName, false);
            using (var ap = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey, writable: true)) ap?.DeleteValue(valueName, false);
        }
    }

    [Fact]
    public async Task OversizedFrameDropsTheConnectionButNotTheServer()
    {
        await using (var c = await _f.ConnectAsync())
        {
            await c.WriteAsync(BitConverter.GetBytes(int.MaxValue));
            await c.FlushAsync();
            var buf = new byte[1];
            int n = await c.ReadAsync(buf, new CancellationTokenSource(5000).Token);
            Assert.Equal(0, n); // server closed the connection
        }
        var hello = await _f.CallAsync<HelloResponse>(new HelloRequest());
        Assert.True(hello.LogicalCores > 0);
    }

    [Fact]
    public void PipeSecurityGrantsInteractiveUsersButNotEveryone()
    {
        var sec = PipeServer.CreateSecurity();
        var rules = sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>().Select(r => r.IdentityReference.Value).ToList();
        Assert.Contains("S-1-5-4", rules);
        Assert.Contains("S-1-5-18", rules);
        Assert.DoesNotContain("S-1-1-0", rules);
    }
}
