using Microsoft.UI.Dispatching;
using PcMonitor.Core;

namespace PcMonitor.Widget.Services;

public readonly record struct SystemSample(double Cpu, double Ram, double Gpu, double NetBps, double DiskBps);

[Flags]
public enum LiveDemand { None = 0, Compact = 1, Dashboard = 2 }

/// <summary>
/// Owns the live subscription. A subscription only exists while some window that needs live data is visible:
/// hide everything and the pipe is closed, so the collector builds no frames and this process does no work.
/// </summary>
public sealed class LiveService
{
    public const int HistoryLength = 120;

    private readonly DispatcherQueue _ui;
    private readonly CollectorClient _client;
    private readonly string? _pipeName;
    private readonly List<SystemSample> _history = new(HistoryLength + 1);
    private CancellationTokenSource? _streamCts;
    private LiveDemand _demand;
    private LiveDetail _activeDetail = LiveDetail.Compact;
    private LiveFrame? _pending;
    private int _flushQueued;
    private bool _seeded;

    public LiveFrame? Latest { get; private set; }
    public HelloResponse? Info { get; private set; }
    public bool Connected { get; private set; }
    public IReadOnlyList<SystemSample> History => _history;

    /// <summary>Raised on the UI thread for every new frame and on connection changes.</summary>
    public event Action? Updated;

    public LiveService(DispatcherQueue ui, CollectorClient client, string? pipeName = null)
    {
        _ui = ui;
        _client = client;
        _pipeName = pipeName;
    }

    public void SetDemand(LiveDemand demand, bool on)
    {
        var next = on ? _demand | demand : _demand & ~demand;
        if (next == _demand) return;
        _demand = next;
        Restart();
    }

    private void Restart()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        if (_demand == LiveDemand.None)
        {
            Connected = false;
            Latest = null;     // stale numbers must not be shown when the window is shown again
            Updated?.Invoke();
            return;
        }

        _activeDetail = _demand.HasFlag(LiveDemand.Dashboard) ? LiveDetail.Full : LiveDetail.Compact;
        var cts = _streamCts = new CancellationTokenSource();
        var detail = _activeDetail;
        _ = Task.Run(() => CollectorClient.RunLiveStreamAsync(_pipeName, detail, 3, OnFrameOffThread, OnConnectionOffThread, cts.Token));
        _ = SeedAsync();
    }

    private async Task SeedAsync()
    {
        try
        {
            var hello = await _client.CallAsync<HelloResponse>(new HelloRequest()).ConfigureAwait(false);
            var hist = _seeded ? null : await _client.CallAsync<LiveHistoryResponse>(new LiveHistoryRequest()).ConfigureAwait(false);
            _ui.TryEnqueue(() =>
            {
                Info = hello;
                if (hist != null && !_seeded)
                {
                    _seeded = true;
                    double total = Math.Max(1, hello.RamTotalBytes);
                    var seeded = hist.Points.TakeLast(HistoryLength)
                        .Select(p => new SystemSample(p.Cpu, p.Ram / total * 100, p.Gpu, p.NetRx + p.NetTx, p.DiskRead + p.DiskWrite)).ToList();
                    _history.InsertRange(0, seeded);
                    TrimHistory();
                }
                Updated?.Invoke();
            });
        }
        catch { /* collector offline; the stream loop keeps retrying and will trigger a reseed via Hello */ }
    }

    private void OnConnectionOffThread(bool connected)
    {
        _ui.TryEnqueue(() =>
        {
            bool was = Connected;
            Connected = connected;
            if (connected && !was) _ = SeedAsync();
            if (was != connected) Updated?.Invoke();
        });
    }

    private void OnFrameOffThread(LiveFrame frame)
    {
        Volatile.Write(ref _pending, frame);
        // Coalesce: if the UI thread is busy, only the newest frame is applied.
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            _ui.TryEnqueue(Flush);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushQueued, 0);
        var f = Interlocked.Exchange(ref _pending, null);
        if (f == null || _demand == LiveDemand.None) return;

        Latest = f;
        Connected = true;
        var s = f.System;
        _history.Add(new SystemSample(s.CpuPct,
            s.RamTotalBytes > 0 ? (double)s.RamUsedBytes / s.RamTotalBytes * 100 : 0,
            s.GpuPct, s.NetRxBps + s.NetTxBps, s.DiskReadBps + s.DiskWriteBps));
        TrimHistory();
        Updated?.Invoke();
    }

    private void TrimHistory()
    {
        if (_history.Count > HistoryLength) _history.RemoveRange(0, _history.Count - HistoryLength);
    }
}
