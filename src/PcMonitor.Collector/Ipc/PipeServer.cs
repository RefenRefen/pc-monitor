using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcMonitor.Core;

namespace PcMonitor.Collector.Ipc;

/// <summary>
/// Named pipe server. The pipe is reachable only by SYSTEM, Administrators and interactively logged-on local users
/// (never over the network). A connection is either request/response, or - after a <see cref="SubscribeRequest"/> -
/// a one-way stream of <see cref="LiveFrame"/> messages.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public const int MaxInstances = 32;

    private readonly CollectorContext _ctx;
    private readonly HistoryService _history;
    private readonly CommandHandler _commands;
    private readonly string _pipeName;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _sessions;

    public int ActiveSessions => Volatile.Read(ref _sessions);

    public PipeServer(CollectorContext ctx, string? pipeName = null)
    {
        _ctx = ctx;
        _log = ctx.Log;
        _pipeName = pipeName ?? PipeNames.Main;
        _history = new HistoryService(ctx);
        _commands = new CommandHandler(ctx);
    }

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    public static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The account running the server needs CreateNewInstance to open further instances of the pipe
        // (LocalSystem is covered above; this keeps unelevated console/dev runs working too).
        using (var self = WindowsIdentity.GetCurrent())
            if (self.User != null)
                ps.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        // S-1-5-4: INTERACTIVE - users logged on at the console / via RDP, not network logons.
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return ps;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var security = CreateSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                Interlocked.Increment(ref _sessions);
                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(connected, ct).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref _sessions); await connected.DisposeAsync().ConfigureAwait(false); }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.LogWarning(ex, "Pipe accept failed; retrying");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var msg = await PipeFraming.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (msg == null) return;

                if (msg is SubscribeRequest sub)
                {
                    await StreamAsync(pipe, sub, ct).ConfigureAwait(false);
                    return;
                }

                PipeMessage response;
                try { response = Handle(msg, pipe); }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Request {Type} failed", msg.GetType().Name);
                    response = new ErrorResponse { Message = ex.Message };
                }
                response.Id = msg.Id;
                await PipeFraming.WriteAsync(pipe, response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* client went away */ }
        catch (InvalidDataException ex) { _log.LogDebug(ex, "Bad frame from client"); }
        catch (System.Text.Json.JsonException ex) { _log.LogDebug(ex, "Bad message from client"); }
    }

    private PipeMessage Handle(PipeMessage msg, NamedPipeServerStream pipe)
    {
        switch (msg)
        {
            case HelloRequest:
                return new HelloResponse
                {
                    ServerVersion = typeof(PipeServer).Assembly.GetName().Version?.ToString() ?? "1.0",
                    EtwAvailable = _ctx.EtwAvailable(),
                    GpuAvailable = _ctx.Gpu.Latest.Available,
                    IsElevated = _ctx.IsElevated,
                    LogicalCores = _ctx.Table.LogicalCores,
                    RamTotalBytes = _ctx.LastTick?.System.RamTotalBytes ?? 0,
                };
            case LiveHistoryRequest:
                return _history.GetLiveHistory();
            case SummaryRequest r:
                return _history.GetSummary(r.FromUnix, r.ToUnix);
            case SeriesRequest r:
                return _history.GetSeries(r.FromUnix, r.ToUnix, r.BucketSeconds, r.AppId);
            case AppDetailRequest r:
                return _history.GetDetail(r.AppId, r.FromUnix, r.ToUnix);
            case ReportContextRequest r:
                _ctx.User.Report(r.VisibleWindowPids, r.IdleSeconds);
                return new OkResponse();
            case CommandRequest r:
                return _commands.Execute(r, pipe);
            default:
                return new ErrorResponse { Message = $"Unsupported request {msg.GetType().Name}" };
        }
    }

    private async Task StreamAsync(NamedPipeServerStream pipe, SubscribeRequest request, CancellationToken serverCt)
    {
        var sub = _ctx.Broadcaster.Subscribe(request.Detail, request.TopN);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        try
        {
            // Acknowledge, then push frames. The reader notices disconnects and unsubscribe requests.
            await PipeFraming.WriteAsync(pipe, new OkResponse { Id = request.Id }, linked.Token).ConfigureAwait(false);

            // Send the current state right away so a new subscriber does not wait up to a second.
            if (_ctx.LastTick is { } last)
                await PipeFraming.WriteAsync(pipe, LiveBroadcaster.BuildFrame(last, _ctx.Identity.GetNameAndPath,
                    request.Detail == LiveDetail.Full ? int.MaxValue : sub.TopN), linked.Token).ConfigureAwait(false);

            var reader = Task.Run(async () =>
            {
                try
                {
                    // A subscribed connection is one-way: any message from the client (or EOF) ends the stream.
                    await PipeFraming.ReadAsync(pipe, linked.Token).ConfigureAwait(false);
                }
                catch { /* disconnect */ }
                finally { linked.Cancel(); }
            }, CancellationToken.None);

            await foreach (var frame in sub.Frames.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                await PipeFraming.WriteAsync(pipe, frame, linked.Token).ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { _ctx.Broadcaster.Unsubscribe(sub); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _acceptLoop?.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
