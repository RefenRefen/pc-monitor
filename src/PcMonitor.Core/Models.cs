namespace PcMonitor.Core;

public enum ResourceKind { Cpu, Ram, Gpu, Network, Disk }

[Flags]
public enum AppFlags
{
    None = 0,
    /// <summary>No valid digital signature.</summary>
    Unsigned = 1 << 0,
    /// <summary>Runs from %TEMP%, %AppData%, Downloads or another user-writable location.</summary>
    UserWritablePath = 1 << 1,
    /// <summary>No visible window but heavy CPU/GPU/network use for a sustained period.</summary>
    HiddenHeavyUsage = 1 << 2,
    /// <summary>Talked to a very large number of distinct remote hosts recently.</summary>
    ManyRemoteHosts = 1 << 3,
    /// <summary>Sustained GPU use while the user is idle (crypto-miner pattern).</summary>
    IdleGpuUsage = 1 << 4,
    /// <summary>Signature present but failed validation (tampered or untrusted).</summary>
    InvalidSignature = 1 << 5,
}

public enum SignatureState { Unknown = 0, Signed = 1, Unsigned = 2, Invalid = 3 }

/// <summary>System-wide values for one sampling tick.</summary>
public sealed class SystemTotals
{
    public double CpuPct { get; set; }
    public long RamUsedBytes { get; set; }
    public long RamTotalBytes { get; set; }
    public double GpuPct { get; set; }
    public long VramUsedBytes { get; set; }
    public long NetRxBps { get; set; }
    public long NetTxBps { get; set; }
    public long DiskReadBps { get; set; }
    public long DiskWriteBps { get; set; }
    public int ProcessCount { get; set; }
}

/// <summary>Live usage of one application (all processes sharing one executable path).</summary>
public sealed class AppUsage
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string? ExePath { get; set; }
    /// <summary>Percent of total machine CPU capacity (all logical cores = 100).</summary>
    public double CpuPct { get; set; }
    /// <summary>Private working set (matches Task Manager's Memory column).</summary>
    public long PrivateBytes { get; set; }
    /// <summary>Commit / private bytes - the best signal for leaks.</summary>
    public long CommitBytes { get; set; }
    public double GpuPct { get; set; }
    public long VramDedicatedBytes { get; set; }
    public long VramSharedBytes { get; set; }
    public long NetRxBps { get; set; }
    public long NetTxBps { get; set; }
    public long DiskReadBps { get; set; }
    public long DiskWriteBps { get; set; }
    public int ProcessCount { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public double Impact { get; set; }
    public AppFlags Flags { get; set; }
}

/// <summary>Live usage of one process, shown when an app is expanded.</summary>
public sealed class ProcessUsage
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>For svchost: the hosted services. For others: empty.</summary>
    public string? Label { get; set; }
    public string? CommandLine { get; set; }
    public double CpuPct { get; set; }
    public long PrivateBytes { get; set; }
    public long CommitBytes { get; set; }
    public double GpuPct { get; set; }
    public long NetRxBps { get; set; }
    public long NetTxBps { get; set; }
    public long DiskReadBps { get; set; }
    public long DiskWriteBps { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public long StartTimeUnix { get; set; }
    public bool HasVisibleWindow { get; set; }
}

public enum StartupKind { RunKey, RunOnceKey, StartupFolder, ScheduledTask, Service, Driver }

public sealed class StartupEntry
{
    public StartupKind Kind { get; set; }
    /// <summary>"Machine" or the user's name/SID for per-user entries.</summary>
    public string Scope { get; set; } = "Machine";
    /// <summary>Registry key, folder or task path.</summary>
    public string Location { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>True when the UI may toggle it through the StartupApproved mechanism.</summary>
    public bool CanDisable { get; set; }
    /// <summary>Server-side only hint: the StartupApproved registry key (HKLM\... or HKU\sid\...) that records enabled state.</summary>
    public string? ApprovedKey { get; set; }
}

/// <summary>Everything we can learn about where an executable came from and why it runs.</summary>
public sealed class AppIdentity
{
    public long AppId { get; set; }
    public string ExePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ProductName { get; set; }
    public string? Company { get; set; }
    public string? Version { get; set; }
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public SignatureState Signature { get; set; }
    public string? Signer { get; set; }
    public string? InstallName { get; set; }
    public string? InstallPublisher { get; set; }
    public string? InstallLocation { get; set; }
    public string? InstallDate { get; set; }
    public string? UninstallString { get; set; }
    public string? PackageFullName { get; set; }
    /// <summary>Resolved logo file for Store/AppX apps (null otherwise).</summary>
    public string? PackageLogoPath { get; set; }
    public List<StartupEntry> Startup { get; set; } = new();
    public AppFlags StaticFlags { get; set; }
    public long FirstSeenUnix { get; set; }
    public long LastSeenUnix { get; set; }
    public bool FileExists { get; set; } = true;
    /// <summary>False until the background resolver has finished hashing/signature checks.</summary>
    public bool Complete { get; set; }
}

/// <summary>One bucket of the time series. Rates are per second; percentages are averages.</summary>
public sealed class SeriesPoint
{
    public long Ts { get; set; }
    public double Cpu { get; set; }
    public double CpuMax { get; set; }
    public double Ram { get; set; }
    public double Gpu { get; set; }
    public double GpuMax { get; set; }
    public double Vram { get; set; }
    public double NetRx { get; set; }
    public double NetTx { get; set; }
    public double DiskRead { get; set; }
    public double DiskWrite { get; set; }
}

/// <summary>Per-app aggregate over a time range.</summary>
public sealed class AppSummary
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string? ExePath { get; set; }
    public string? Publisher { get; set; }
    public SignatureState Signature { get; set; }
    public AppFlags Flags { get; set; }
    public double AvgCpu { get; set; }
    public double MaxCpu { get; set; }
    public double AvgRam { get; set; }
    public double MaxRam { get; set; }
    public double MaxCommit { get; set; }
    public double AvgGpu { get; set; }
    public double MaxGpu { get; set; }
    public double MaxVram { get; set; }
    public long NetRxBytes { get; set; }
    public long NetTxBytes { get; set; }
    public long DiskReadBytes { get; set; }
    public long DiskWriteBytes { get; set; }
    public long LastSeenUnix { get; set; }
    public double Impact { get; set; }
}

public sealed class ConnectionInfo
{
    public string RemoteIp { get; set; } = "";
    public string? RemoteHost { get; set; }
    public int Port { get; set; }
    public string Proto { get; set; } = "tcp";
    public long Bytes { get; set; }
    public long FirstSeenUnix { get; set; }
    public long LastSeenUnix { get; set; }
}
