using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PcMonitor.Core;
using PcMonitor.Widget.Controls;
using PcMonitor.Widget.Native;
using PcMonitor.Widget.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace PcMonitor.Widget;

/// <summary>The always-on-top glass pill: current totals with two minutes of sparklines, plus the three heaviest apps.</summary>
public sealed partial class CompactWindow : Window
{
    private const double WidthDip = 308;
    private const double SnapDip = 14;

    private readonly MetricRow _cpu = new("CPU", Theme.Cpu);
    private readonly MetricRow _ram = new("RAM", Theme.Ram);
    private readonly MetricRow _gpu = new("GPU", Theme.Gpu);
    private readonly MetricRow _net = new("NET", Theme.Net);
    private readonly MetricRow _disk = new("DISK", Theme.Disk);
    private readonly CompactAppRow[] _apps;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Win32.SubclassProc _subclass;
    private Windows.Foundation.Point _pressPoint;
    private bool _pressed;
    private bool _positioned;

    public bool IsShown { get; private set; }

    public CompactWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _subclass = SubclassCallback;

        SystemBackdrop = new GlassBackdrop();
        ExtendsContentIntoTitleBar = true;
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = App.Settings.AlwaysOnTop;
        }
        _appWindow.IsShownInSwitchers = false;
        Win32.StripFrame(_hwnd);
        Win32.RoundCorners(_hwnd);
        Win32.SetWindowSubclass(_hwnd, _subclass, (UIntPtr)1, UIntPtr.Zero);

        foreach (var row in new[] { _cpu, _ram, _gpu, _net, _disk }) Metrics.Children.Add(row);
        _apps = Enumerable.Range(0, 3).Select(_ => new CompactAppRow(App.Icons)).ToArray();
        foreach (var r in _apps) Apps.Children.Add(r);

        TopItem.IsChecked = App.Settings.AlwaysOnTop;

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += (_, _) => _pressed = false;
        Root.DoubleTapped += (_, e) => { if (e.OriginalSource is not Microsoft.UI.Xaml.Controls.Button) App.Instance.ShowDashboard(); };
        Root.Loaded += (_, _) => { Root.XamlRoot.Changed += (_, _) => FitToContent(); FitToContent(); };
        Closed += (_, e) => { if (!App.Instance.Exiting) { e.Handled = true; App.Instance.HideCompact(); } };

        App.Live.Updated += Refresh;
        Refresh();
        FitToContent();
    }

    // ------------------------------------------------------------------ show / hide / placement

    public void ShowWidget()
    {
        if (!_positioned) { PlaceInitial(); _positioned = true; }
        _appWindow.Show(false);
        IsShown = true;
    }

    public void HideWidget()
    {
        _appWindow.Hide();
        IsShown = false;
    }

    public void SetAlwaysOnTop(bool on)
    {
        if (_appWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = on;
        TopItem.IsChecked = on;
    }

    private double Scale => Math.Max(1.0, Win32.GetDpiForWindow(_hwnd) / 96.0);

    private void FitToContent()
    {
        double scale = Scale;
        Frame.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        int h = (int)Math.Ceiling((Frame.DesiredSize.Height + 2) * scale);
        int w = (int)Math.Ceiling(WidthDip * scale);
        var size = _appWindow.Size;
        if (size.Width == w && size.Height == h) return;

        if (IsShown && Win32.GetWindowRect(_hwnd, out var rc))
        {
            // Grow/shrink away from the screen edge the widget is docked to, so it never slides off-screen.
            var work = Win32.GetWorkArea(Win32.MonitorFromRect(ref rc, Win32.MONITOR_DEFAULTTONEAREST));
            int y = work.Bottom - rc.Bottom < (int)(60 * scale) ? rc.Bottom - h : rc.Top;
            _appWindow.MoveAndResize(new RectInt32(rc.Left, y, w, h));
        }
        else _appWindow.Resize(new SizeInt32(w, h));
    }

    private void PlaceInitial()
    {
        FitToContent();
        var s = App.Settings;
        var size = _appWindow.Size;
        if (s.CompactX is int x && s.CompactY is int y && IsOnAnyScreen(x, y, size))
        {
            _appWindow.Move(new PointInt32(x, y));
            return;
        }
        var pt = new Win32.POINT { X = 0, Y = 0 };
        var work = Win32.GetWorkArea(Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTOPRIMARY));
        int margin = (int)(16 * Scale);
        _appWindow.Move(new PointInt32(work.Right - size.Width - margin, work.Bottom - size.Height - margin));
    }

    private static bool IsOnAnyScreen(int x, int y, SizeInt32 size)
    {
        var rc = new Win32.RECT { Left = x, Top = y, Right = x + size.Width, Bottom = y + size.Height };
        var mon = Win32.MonitorFromRect(ref rc, 0 /* MONITOR_DEFAULTTONULL */);
        if (mon == IntPtr.Zero) return false;
        var work = Win32.GetWorkArea(mon);
        return x + 60 < work.Right && x + size.Width - 60 > work.Left && y >= work.Top - 4 && y + 40 < work.Bottom;
    }

    // ------------------------------------------------------------------ dragging with edge snapping

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(Root);
        if (!pp.Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _pressPoint = pp.Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        var pp = e.GetCurrentPoint(Root);
        if (!pp.Properties.IsLeftButtonPressed) { _pressed = false; return; }
        if (Math.Abs(pp.Position.X - _pressPoint.X) + Math.Abs(pp.Position.Y - _pressPoint.Y) < 5) return;

        _pressed = false;
        Root.ReleasePointerCaptures();
        Win32.ReleaseCapture();
        // Hands control to the system move loop; WM_MOVING (below) provides the snapping.
        Win32.SendMessageW(_hwnd, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
    }

    private IntPtr SubclassCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == Win32.WM_MOVING)
        {
            var rc = Marshal.PtrToStructure<Win32.RECT>(lParam);
            Snap(ref rc);
            Marshal.StructureToPtr(rc, lParam, false);
        }
        else if (msg == Win32.WM_EXITSIZEMOVE)
        {
            if (Win32.GetWindowRect(hwnd, out var r))
            {
                App.Settings.CompactX = r.Left;
                App.Settings.CompactY = r.Top;
                App.Settings.SaveSoon();
            }
        }
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void Snap(ref Win32.RECT rc)
    {
        var mon = Win32.MonitorFromRect(ref rc, Win32.MONITOR_DEFAULTTONEAREST);
        var work = Win32.GetWorkArea(mon);
        int d = (int)(SnapDip * Scale), w = rc.Width, h = rc.Height;
        if (Math.Abs(rc.Left - work.Left) < d) rc.Left = work.Left;
        else if (Math.Abs(work.Right - rc.Right) < d) rc.Left = work.Right - w;
        if (Math.Abs(rc.Top - work.Top) < d) rc.Top = work.Top;
        else if (Math.Abs(work.Bottom - rc.Bottom) < d) rc.Top = work.Bottom - h;
        rc.Right = rc.Left + w;
        rc.Bottom = rc.Top + h;
    }

    // ------------------------------------------------------------------ live data

    private void Refresh()
    {
        var live = App.Live;
        var f = live.Latest;
        var hist = live.History;

        StatusDot.Fill = Theme.Brush(live.Connected ? Theme.Good : Theme.Warn);
        if (!live.Connected)
            ShowStatus("Collector not reachable. Retrying…", Theme.Warn);
        else if (f != null && !f.EtwAvailable)
            ShowStatus("Network and per-app disk detail need the collector to run elevated.", Theme.Faint.Color);
        else
            ShowStatus(null, default);

        if (f == null) return;
        var s = f.System;

        _cpu.Update($"{s.CpuPct:0}%", $"{s.ProcessCount} processes", hist.Select(h => h.Cpu), 100);

        double ramPct = s.RamTotalBytes > 0 ? (double)s.RamUsedBytes / s.RamTotalBytes * 100 : 0;
        _ram.Update($"{Format.Bytes(s.RamUsedBytes)} / {Format.Bytes(s.RamTotalBytes, 0)}", $"{ramPct:0}% in use", hist.Select(h => h.Ram), 100);

        _gpu.Update(f.GpuAvailable ? $"{s.GpuPct:0}%" : "n/a",
            f.GpuAvailable && s.VramUsedBytes > 0 ? $"VRAM {Format.Bytes(s.VramUsedBytes)}" : null,
            hist.Select(h => h.Gpu), 100);

        double netMax = Math.Max(128 * 1024, hist.Count == 0 ? 0 : hist.Max(h => h.NetBps));
        _net.Update(f.EtwAvailable ? "? " + Format.Rate(s.NetRxBps) : "n/a",
            f.EtwAvailable ? "? " + Format.Rate(s.NetTxBps) : null, hist.Select(h => h.NetBps), netMax);

        double diskMax = Math.Max(512 * 1024, hist.Count == 0 ? 0 : hist.Max(h => h.DiskBps));
        _disk.Update("R " + Format.Rate(s.DiskReadBps), "W " + Format.Rate(s.DiskWriteBps), hist.Select(h => h.DiskBps), diskMax);

        var top = f.Apps.OrderByDescending(a => a.Impact).Take(_apps.Length).ToList();
        for (int i = 0; i < _apps.Length; i++) _apps[i].Update(i < top.Count ? top[i] : null);
    }

    private void ShowStatus(string? text, Windows.UI.Color color)
    {
        var wanted = text == null ? Visibility.Collapsed : Visibility.Visible;
        bool changed = StatusText.Visibility != wanted;
        if (text != null)
        {
            StatusText.Text = text;
            StatusText.Foreground = Theme.Brush(color);
        }
        StatusText.Visibility = wanted;
        if (changed) FitToContent();
    }

    // ------------------------------------------------------------------ menu handlers

    private void OnOpenDashboard(object sender, RoutedEventArgs e) => App.Instance.ShowDashboard();
    private void OnToggleTop(object sender, RoutedEventArgs e) => App.Instance.SetAlwaysOnTop(TopItem.IsChecked);
    private void OnHide(object sender, RoutedEventArgs e) => App.Instance.HideCompact();
    private void OnExit(object sender, RoutedEventArgs e) => App.Instance.ExitApp();
}
