using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcMonitor.Collector.Ipc;

namespace PcMonitor.Collector;

public sealed class CollectorOptions
{
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMonitor");
    public string? PipeName { get; set; }
}

internal sealed class CollectorWorker : BackgroundService
{
    private readonly ILogger<CollectorWorker> _log;
    private readonly CollectorOptions _options;

    public CollectorWorker(ILogger<CollectorWorker> log, IOptions<CollectorOptions> options)
    {
        _log = log;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // let the host finish starting (service control manager is waiting)

        // Low priority: this process must never compete with the things it is watching.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not fatal */ }

        try { DataDirectorySecurity.Apply(_options.DataDirectory); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not restrict access to {Dir}", _options.DataDirectory); }

        string dbPath = Path.Combine(_options.DataDirectory, "usage.db");
        using var engine = new CollectorEngine(dbPath, _log);
        using var server = new PipeServer(engine.Context, _options.PipeName);
        server.Start();
        _log.LogInformation("Collector running (elevated: {Elevated}, data: {Path})", engine.Context.IsElevated, dbPath);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long tickNo = 0;
        try
        {
            do
            {
                try { engine.Tick(); }
                catch (Exception ex) { _log.LogError(ex, "Tick failed"); }

                // Start-up code, JIT data and one-off buffers stay resident long after they are needed:
                // compact the heap and hand those pages back once the first minute is over, then every 10 minutes.
                if (++tickNo == 30 || tickNo % 600 == 0) MemoryTrim.Run();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }

        _log.LogInformation("Collector stopping");
    }
}
