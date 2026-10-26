using System.Text.Json.Serialization;

namespace PcMonitor.Core;

/// <summary>Named pipe protocol: length-prefixed UTF-8 JSON messages.</summary>
public static class PipeNames
{
    public const string Main = "PcMonitor.Collector.v1";
}

public enum LiveDetail
{
    /// <summary>Totals + top few apps (compact widget).</summary>
    Compact = 0,
    /// <summary>Totals + every active app (dashboard).</summary>
    Full = 1,
}

public enum CommandKind
{
    EndProcess = 0,
    EndApp = 1,
    DisableStartup = 2,
    EnableStartup = 3,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(HelloResponse), "helloR")]
[JsonDerivedType(typeof(SubscribeRequest), "sub")]
[JsonDerivedType(typeof(LiveFrame), "frame")]
[JsonDerivedType(typeof(LiveHistoryRequest), "liveHist")]
[JsonDerivedType(typeof(LiveHistoryResponse), "liveHistR")]
[JsonDerivedType(typeof(SummaryRequest), "sum")]
[JsonDerivedType(typeof(SummaryResponse), "sumR")]
[JsonDerivedType(typeof(SeriesRequest), "series")]
[JsonDerivedType(typeof(SeriesResponse), "seriesR")]
[JsonDerivedType(typeof(AppDetailRequest), "detail")]
[JsonDerivedType(typeof(AppDetailResponse), "detailR")]
[JsonDerivedType(typeof(ReportContextRequest), "ctx")]
[JsonDerivedType(typeof(CommandRequest), "cmd")]
[JsonDerivedType(typeof(CommandResponse), "cmdR")]
[JsonDerivedType(typeof(OkResponse), "ok")]
[JsonDerivedType(typeof(ErrorResponse), "err")]
public abstract class PipeMessage
{
    /// <summary>Correlates a response with its request.</summary>
    public int Id { get; set; }
}

public sealed class HelloRequest : PipeMessage
{
    public int ProtocolVersion { get; set; } = 1;
}

public sealed class HelloResponse : PipeMessage
{
    public int ProtocolVersion { get; set; } = 1;
    public string ServerVersion { get; set; } = "";
    public bool EtwAvailable { get; set; }
    public bool GpuAvailable { get; set; }
    public bool IsElevated { get; set; }
    public int LogicalCores { get; set; }
    public long RamTotalBytes { get; set; }
}

/// <summary>Turns this connection into a live stream of <see cref="LiveFrame"/> messages (1 per second).</summary>
public sealed class SubscribeRequest : PipeMessage
{
    public LiveDetail Detail { get; set; }
    /// <summary>How many apps a Compact subscriber wants.</summary>
    public int TopN { get; set; } = 5;
}

public sealed class LiveFrame : PipeMessage
{
    public long Ts { get; set; }
    public SystemTotals System { get; set; } = new();
    public List<AppUsage> Apps { get; set; } = new();
    public bool EtwAvailable { get; set; }
    public bool GpuAvailable { get; set; }
}

public sealed class LiveHistoryRequest : PipeMessage { }

/// <summary>Last ~10 minutes at 1 s resolution (ring buffer), system totals only.</summary>
public sealed class LiveHistoryResponse : PipeMessage
{
    public List<SeriesPoint> Points { get; set; } = new();
}

public sealed class SummaryRequest : PipeMessage
{
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
}

public sealed class SummaryResponse : PipeMessage
{
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
    public List<AppSummary> Apps { get; set; } = new();
}

public sealed class SeriesRequest : PipeMessage
{
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
    /// <summary>0 = choose automatically.</summary>
    public int BucketSeconds { get; set; }
    /// <summary>Null = whole system (sum of all apps).</summary>
    public long? AppId { get; set; }
}

public sealed class SeriesResponse : PipeMessage
{
    public int BucketSeconds { get; set; }
    public List<SeriesPoint> Points { get; set; } = new();
}

public sealed class AppDetailRequest : PipeMessage
{
    public long AppId { get; set; }
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
}

public sealed class AppDetailResponse : PipeMessage
{
    public AppIdentity? Identity { get; set; }
    public AppUsage? Live { get; set; }
    public List<ProcessUsage> Processes { get; set; } = new();
    public List<ConnectionInfo> Connections { get; set; } = new();
    public SeriesResponse Series { get; set; } = new();
}

/// <summary>
/// The widget runs in the user session and can see windows and input idle time,
/// which a session-0 service cannot. It reports them periodically.
/// </summary>
public sealed class ReportContextRequest : PipeMessage
{
    public List<int> VisibleWindowPids { get; set; } = new();
    public int IdleSeconds { get; set; }
}

public sealed class CommandRequest : PipeMessage
{
    public CommandKind Command { get; set; }
    public int Pid { get; set; }
    public long AppId { get; set; }
    /// <summary>For startup commands: the entry to change.</summary>
    public StartupEntry? Startup { get; set; }
}

public sealed class CommandResponse : PipeMessage
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class OkResponse : PipeMessage { }

public sealed class ErrorResponse : PipeMessage
{
    public string Message { get; set; } = "";
}
