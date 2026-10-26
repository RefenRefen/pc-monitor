using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcMonitor.Collector;

// Runs as a Windows service (installed by the installer) or directly in a console for development:
//   PcMonitor.Collector.exe [--data <dir>] [--pipe <name>]
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "PcMonitorCollector");
builder.Services.Configure<CollectorOptions>(o =>
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--data") o.DataDirectory = args[i + 1];
        else if (args[i] == "--pipe") o.PipeName = args[i + 1];
    }
});
builder.Services.AddHostedService<CollectorWorker>();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Build().Run();
