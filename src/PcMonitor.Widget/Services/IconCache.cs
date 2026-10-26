using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PcMonitor.Widget.Native;

namespace PcMonitor.Widget.Services;

/// <summary>
/// App icons: extracted once through the shell on a dedicated STA thread, cached as PNG on disk
/// (%LocalAppData%\PcMonitor\icons) and as decoded bitmaps in memory.
/// </summary>
public sealed class IconCache
{
    private const int ExtractSize = 48;
    private const int MaxMemoryEntries = 400;

    private readonly DispatcherQueue _ui;
    private readonly string _folder = Path.Combine(AppSettings.Folder, "icons");
    private readonly Dictionary<long, ImageSource> _memory = new();
    private readonly Dictionary<long, List<Action<ImageSource>>> _waiters = new();
    private readonly BlockingCollection<(long AppId, string? ExePath, string? LogoPath)> _queue = new();
    private Thread? _thread;

    public IconCache(DispatcherQueue ui) => _ui = ui;

    /// <summary>Cached image or null. UI thread only.</summary>
    public ImageSource? TryGet(long appId) => _memory.TryGetValue(appId, out var s) ? s : null;

    /// <summary>Sets <paramref name="image"/>.Source now if cached, otherwise as soon as the icon is ready.</summary>
    public void Attach(Image image, long appId, string? exePath, string? logoPath = null)
    {
        image.Tag = appId;
        if (_memory.TryGetValue(appId, out var cached)) { image.Source = cached; return; }
        image.Source = null;
        Request(appId, exePath, logoPath, src =>
        {
            if (image.Tag is long id && id == appId) image.Source = src;   // row may have been recycled meanwhile
        });
    }

    public void Request(long appId, string? exePath, string? logoPath, Action<ImageSource> ready)
    {
        if (_memory.TryGetValue(appId, out var cached)) { ready(cached); return; }
        if (_waiters.TryGetValue(appId, out var list)) { list.Add(ready); return; }
        _waiters[appId] = new List<Action<ImageSource>> { ready };
        if (string.IsNullOrEmpty(exePath) && string.IsNullOrEmpty(logoPath)) return;

        EnsureThread();
        _queue.Add((appId, exePath, logoPath));
    }

    private void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(Worker) { IsBackground = true, Name = "icons", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Worker()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            string png = Path.Combine(_folder, job.AppId.ToString("x16") + ".png");
            string? file = null;
            try
            {
                if (File.Exists(png)) file = png;
                else if (job.LogoPath != null && File.Exists(job.LogoPath)) file = job.LogoPath;
                else if (job.ExePath != null && File.Exists(job.ExePath) && ShellIcons.TrySavePng(job.ExePath, png, ExtractSize)) file = png;
            }
            catch { /* no icon: the placeholder glyph stays */ }

            if (file == null) continue;
            long id = job.AppId;
            string path = file;
            _ui.TryEnqueue(() =>
            {
                try
                {
                    var bmp = new BitmapImage { DecodePixelWidth = ExtractSize };
                    bmp.UriSource = new Uri(path);
                    if (_memory.Count >= MaxMemoryEntries) _memory.Clear();
                    _memory[id] = bmp;
                    if (_waiters.Remove(id, out var list)) foreach (var cb in list) cb(bmp);
                }
                catch { _waiters.Remove(id); }
            });
        }
    }
}