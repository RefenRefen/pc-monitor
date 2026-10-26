using System.IO.Pipes;
using PcMonitor.Core;

namespace PcMonitor.Widget.Services;

public sealed class CollectorUnavailableException : Exception
{
    public CollectorUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Request/response access to the collector service. One lazily opened connection, re-opened on failure.
/// Nothing is kept open when nobody asks for data.
/// </summary>
public sealed class CollectorClient : IDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private int _nextId;

    public CollectorClient(string? pipeName = null) => _pipeName = pipeName ?? PipeNames.Main;

    internal static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            throw new CollectorUnavailableException("The collector service is not reachable.", ex);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task<T> CallAsync<T>(PipeMessage request, CancellationToken ct = default, int timeoutMs = 15000) where T : PipeMessage
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                try
                {
                    _pipe ??= await ConnectAsync(_pipeName, 1500, timeout.Token).ConfigureAwait(false);
                    request.Id = Interlocked.Increment(ref _nextId);
                    await PipeFraming.WriteAsync(_pipe, request, timeout.Token).ConfigureAwait(false);
                    var response = await PipeFraming.ReadAsync(_pipe, timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("Collector closed the connection.");
                    if (response is ErrorResponse err) throw new InvalidOperationException(err.Message);
                    if (response is not T typed) throw new InvalidDataException($"Unexpected response {response.GetType().Name}");
                    return typed;
                }
                catch (IOException) when (attempt == 0)
                {
                    ResetPipe(); // stale connection (service restarted): retry once with a fresh one
                }
                catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
                {
                    ResetPipe();
                    if (ex is CollectorUnavailableException) throw;
                    throw new CollectorUnavailableException("Lost connection to the collector.", ex);
                }
                catch (OperationCanceledException)
                {
                    ResetPipe();
                    throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    private void ResetPipe()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose() { ResetPipe(); _gate.Dispose(); }

    /// <summary>
    /// Keeps a live subscription going (reconnecting with back-off) until cancelled.
    /// Frames and connection state are delivered on a thread-pool thread.
    /// </summary>
    public static async Task RunLiveStreamAsync(string? pipeName, LiveDetail detail, int topN,
        Action<LiveFrame> onFrame, Action<bool> onConnection, CancellationToken ct)
    {
        pipeName ??= PipeNames.Main;
        int delayMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = await ConnectAsync(pipeName, 2000, ct).ConfigureAwait(false);
                await PipeFraming.WriteAsync(pipe, new SubscribeRequest { Id = 1, Detail = detail, TopN = topN }, ct).ConfigureAwait(false);
                var ack = await PipeFraming.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (ack is not OkResponse) throw new IOException("Subscription refused");

                onConnection(true);
                delayMs = 1000;
                while (await PipeFraming.ReadAsync(pipe, ct).ConfigureAwait(false) is { } msg)
                    if (msg is LiveFrame frame) onFrame(frame);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* fall through to back-off */ }

            if (ct.IsCancellationRequested) break;
            onConnection(false);
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            delayMs = Math.Min(delayMs * 2, 10_000);
        }
    }
}
