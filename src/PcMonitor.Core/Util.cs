using System.Globalization;

namespace PcMonitor.Core;

/// <summary>Stable 64-bit identifier for an application, derived from its full exe path.</summary>
public static class AppKey
{
    public static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

    /// <summary>FNV-1a 64 over the normalised path. Stable across runs and machines.</summary>
    public static long FromPath(string path)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (char c in Normalize(path))
        {
            h ^= (byte)(c & 0xFF); h *= prime;
            h ^= (byte)(c >> 8); h *= prime;
        }
        return unchecked((long)h);
    }
}

public static class TimeUtil
{
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static DateTime FromUnixLocal(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
}

/// <summary>Chooses a chart bucket size so every range yields roughly 200-400 points.</summary>
public static class Resolution
{
    private static readonly int[] NiceSteps =
        { 1, 2, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 21600, 43200, 86400 };

    public const int LiveWindowSeconds = 600;
    public const int MinuteResolutionSeconds = 60;

    public static int PickBucketSeconds(long rangeSeconds, int targetPoints = 300)
    {
        if (rangeSeconds <= 0) return 1;
        double ideal = (double)rangeSeconds / targetPoints;
        int min = rangeSeconds <= LiveWindowSeconds ? 1 : MinuteResolutionSeconds;
        foreach (int step in NiceSteps)
            if (step >= min && step >= ideal) return step;
        return NiceSteps[^1];
    }
}

public static class Format
{
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double bytes, int decimals = 1)
    {
        if (double.IsNaN(bytes) || bytes < 0) bytes = 0;
        int u = 0;
        while (bytes >= 1024 && u < ByteUnits.Length - 1) { bytes /= 1024; u++; }
        string fmt = u == 0 ? "0" : decimals <= 0 ? "0" : "0." + new string('0', decimals);
        return bytes.ToString(fmt, CultureInfo.InvariantCulture) + " " + ByteUnits[u];
    }

    public static string Rate(double bytesPerSec, int decimals = 1) => Bytes(bytesPerSec, decimals) + "/s";

    public static string Percent(double pct) =>
        (pct < 10 ? Math.Round(pct, 1) : Math.Round(pct)).ToString(pct < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";
}
