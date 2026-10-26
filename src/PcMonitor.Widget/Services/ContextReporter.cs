using PcMonitor.Core;
using PcMonitor.Widget.Native;

namespace PcMonitor.Widget.Services;

/// <summary>
/// The collector runs in session 0 and cannot see the desktop. Every 15 seconds the widget tells it which processes
/// own visible windows and how long the user has been idle; the collector uses this to flag hidden heavy usage
/// and GPU work while the user is away.
/// </summary>
public sealed class ContextReporter : IDisposable
{
    private readonly CollectorClient _client;
    private readonly Timer _timer;
    private int _busy;

    public ContextReporter(CollectorClient client)
    {
        _client = client;
        _timer = new Timer(_ => Report(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
    }

    private async void Report()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var req = new ReportContextRequest { IdleSeconds = Win32.GetIdleSeconds() };
            req.VisibleWindowPids.AddRange(Win32.GetProcessesWithVisibleWindows());
            await _client.CallAsync<OkResponse>(req, timeoutMs: 5000).ConfigureAwait(false);
        }
        catch { /* collector offline - try again next time */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
