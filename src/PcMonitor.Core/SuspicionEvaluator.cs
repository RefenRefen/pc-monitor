namespace PcMonitor.Core;

/// <summary>Inputs for one app for one second.</summary>
public readonly record struct SuspicionInput(
    double CpuPct,
    double GpuPct,
    long NetBytesPerSec,
    /// <summary>null = unknown (the widget has not reported window state recently).</summary>
    bool? HasVisibleWindow,
    /// <summary>null = unknown.</summary>
    int? UserIdleSeconds,
    int DistinctRemoteHostsRecent);

/// <summary>
/// Per-app rolling state that turns raw usage into dynamic <see cref="AppFlags"/>.
/// All thresholds are deliberately conservative so a flag means something.
/// </summary>
public sealed class SuspicionState
{
    public const double HeavyCpuPct = 10.0;
    public const double HeavyGpuPct = 20.0;
    public const long HeavyNetBytesPerSec = 1024 * 1024;
    public const int HiddenHeavySecondsToFlag = 60;
    public const int IdleUserSeconds = 120;
    public const int IdleGpuSecondsToFlag = 120;
    public const double IdleGpuPct = 30.0;
    public const int ManyHostsThreshold = 150;

    private int _hiddenHeavySeconds;
    private int _idleGpuSeconds;

    public AppFlags Flags { get; private set; }

    /// <summary>Advance state by <paramref name="seconds"/> and return the dynamic flags.</summary>
    public AppFlags Update(in SuspicionInput input, int seconds = 1)
    {
        var flags = AppFlags.None;

        bool heavy = input.CpuPct >= HeavyCpuPct || input.GpuPct >= HeavyGpuPct || input.NetBytesPerSec >= HeavyNetBytesPerSec;
        if (input.HasVisibleWindow == false && heavy) _hiddenHeavySeconds += seconds;
        else _hiddenHeavySeconds = Math.Max(0, _hiddenHeavySeconds - seconds * 2);
        if (_hiddenHeavySeconds >= HiddenHeavySecondsToFlag) flags |= AppFlags.HiddenHeavyUsage;

        bool idleGpu = input.UserIdleSeconds is int idle && idle >= IdleUserSeconds && input.GpuPct >= IdleGpuPct;
        if (idleGpu) _idleGpuSeconds += seconds;
        else _idleGpuSeconds = 0;
        if (_idleGpuSeconds >= IdleGpuSecondsToFlag) flags |= AppFlags.IdleGpuUsage;

        if (input.DistinctRemoteHostsRecent >= ManyHostsThreshold && input.HasVisibleWindow != true)
            flags |= AppFlags.ManyRemoteHosts;

        Flags = flags;
        return flags;
    }

    /// <summary>Static flags derived from the file location and signature.</summary>
    public static AppFlags StaticFlagsFor(string? exePath, SignatureState signature)
    {
        var f = AppFlags.None;
        if (signature == SignatureState.Unsigned) f |= AppFlags.Unsigned;
        if (signature == SignatureState.Invalid) f |= AppFlags.InvalidSignature;
        if (IsUserWritableLocation(exePath)) f |= AppFlags.UserWritablePath;
        return f;
    }

    private static readonly string[] WritableMarkers =
    {
        @"\appdata\local\temp\", @"\appdata\roaming\", @"\downloads\", @"\windows\temp\",
        @"\users\public\", @"\$recycle.bin\",
    };

    public static bool IsUserWritableLocation(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        string p = exePath.Replace('/', '\\').ToLowerInvariant();
        // Well known, legitimate per-user installs (Teams, Discord, VS Code, OneDrive ...) live in
        // AppData\Local\Programs / AppData\Local\<vendor>; only flag the clearly suspicious spots.
        foreach (var m in WritableMarkers)
            if (p.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }
}
