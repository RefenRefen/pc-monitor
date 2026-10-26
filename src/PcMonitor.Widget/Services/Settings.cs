using System.Text.Json;

namespace PcMonitor.Widget.Services;

/// <summary>Small JSON settings file in %LocalAppData%\PcMonitor. Saves are debounced so dragging never hammers the disk.</summary>
public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcMonitor");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public bool CompactVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public int? CompactX { get; set; }
    public int? CompactY { get; set; }

    /// <summary>Dashboard time range in hours (user selectable; any value from 1 to 2160).</summary>
    public double RangeHours { get; set; } = 24;
    public bool SuspiciousOnly { get; set; }
    public int? DashboardWidth { get; set; }
    public int? DashboardHeight { get; set; }

    private Timer? _debounce;
    private readonly object _gate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file: start fresh */ }
        return new AppSettings();
    }

    public void SaveSoon()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => SaveNow(), null, 600, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are a convenience, never fatal */ }
    }
}
