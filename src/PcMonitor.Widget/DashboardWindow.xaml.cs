using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using PcMonitor.Widget.Controls;
using PcMonitor.Widget.Native;
using PcMonitor.Widget.Services;
using PcMonitor.Widget.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace PcMonitor.Widget;

/// <summary>Full dashboard: any time range, per-resource history, searchable app list and the details panel.</summary>
public sealed partial class DashboardWindow : Window
{
    private sealed class Card
    {
        public Border Root = null!;
        public TextBlock Now = null!;
        public TextBlock Range = null!;
        public AreaChart Chart = null!;
    }

    private readonly CollectorClient _client = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly AppDetailsPanel _details = new();
    private readonly Dictionary<long, AppRowVm> _rows = new();
    private readonly Dictionary<string, TextBlock> _headerArrows = new();
    private readonly Card _cpu, _ram, _gpu, _net, _disk, _summary;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    private List<AppRowVm> _shown = new();
    private string _sortKey = "Impact";
    private bool _sortDesc = true;
    private double _hours;
    private long _selected;
    private bool _busy, _restoring, _closed, _initialized;
    private DateTime _lastRefresh = DateTime.MinValue, _lastDetail = DateTime.MinValue;

    public DashboardWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var tb = _appWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Colors.White;
        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x88, 255, 255, 255);
        Title = "PC Monitor";

        // 5 metric cards + a range summary card
        _cpu = MakeCard("CPU", Theme.Cpu);
        _ram = MakeCard("Memory", Theme.Ram);
        _gpu = MakeCard("GPU", Theme.Gpu);
        _net = MakeCard("Network", Theme.Net);
        _disk = MakeCard("Disk", Theme.Disk);
        _summary = MakeCard("Applications", Windows.UI.Color.FromArgb(0xFF, 0xA0, 0xA8, 0xB8));
        _summary.Chart.Visibility = Visibility.Collapsed;
        for (int c = 0; c < 3; c++) Cards.ColumnDefinitions.Add(new ColumnDefinition());
        Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var cards = new[] { _cpu, _ram, _gpu, _net, _disk, _summary };
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i].Root, i % 3);
            Grid.SetRow(cards[i].Root, i / 3);
            Cards.Children.Add(cards[i].Root);
        }

        BuildListHeader();
        DetailsHost.Children.Add(_details);
        _details.DataChanged += () => _ = RefreshAsync(force: true);

        // restore range
        _hours = Math.Clamp(App.Settings.RangeHours, 1.0 / 6, 2160);
        SelectRange(_hours);
        SuspiciousToggle.IsChecked = App.Settings.SuspiciousOnly;

        // window size / position
        double scale = Math.Max(1.0, Win32.GetDpiForWindow(_hwnd) / 96.0);
        int w = (int)((App.Settings.DashboardWidth ?? 1320) * scale), h = (int)((App.Settings.DashboardHeight ?? 840) * scale);
        var work = Win32.GetWorkArea(Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY));
        w = Math.Min(w, work.Width); h = Math.Min(h, work.Height);
        _appWindow.MoveAndResize(new RectInt32(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h));

        _timer = App.UiQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        App.Live.Updated += OnLiveUpdated;
        Closed += OnClosed;
        UpdateStatus();
        _initialized = true;
        _ = RefreshAsync(force: true);
    }

    public void ShowAndActivate()
    {
        Activate();
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        App.Live.Updated -= OnLiveUpdated;
        double scale = Math.Max(1.0, Win32.GetDpiForWindow(_hwnd) / 96.0);
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            App.Settings.DashboardWidth = (int)(_appWindow.Size.Width / scale);
            App.Settings.DashboardHeight = (int)(_appWindow.Size.Height / scale);
        }
        App.Settings.SaveSoon();
        _client.Dispose();
    }

    // ------------------------------------------------------------------ cards

    private Card MakeCard(string title, Windows.UI.Color color)
    {
        var card = new Card
        {
            Now = new TextBlock { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Theme.Strong },
            Range = new TextBlock { FontSize = 11, Foreground = Theme.Faint, TextWrapping = TextWrapping.Wrap },
            Chart = new AreaChart { Height = 96 },
        };
        var grid = new Grid { RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid();
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(2), Background = Theme.Brush(color) },
                new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.85 },
            },
        });
        head.Children.Add(card.Now);
        grid.Children.Add(head);
        Grid.SetRow(card.Range, 1);
        grid.Children.Add(card.Range);
        Grid.SetRow(card.Chart, 2);
        grid.Children.Add(card.Chart);
        card.Root = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 255, 255, 255)),
            BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1),
        };
        return card;
    }

    // ------------------------------------------------------------------ range handling

    private static readonly double[] PresetHours = { 1.0 / 6, 1, 6, 24, 72, 168, 720, 2160 };

    private void SelectRange(double hours)
    {
        int idx = Array.FindIndex(PresetHours, p => Math.Abs(p - hours) < 0.001);
        _restoring = true;
        try
        {
            if (idx >= 0) RangeBar.SelectedItem = RangeBar.Items[idx];
            else
            {
                RangeBar.SelectedItem = RangeBar.Items[^1];
                CustomHours.Value = Math.Round(hours);
                CustomHours.Visibility = Visibility.Visible;
            }
        }
        finally { _restoring = false; }
    }

    private void OnRangeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_restoring || !_initialized || sender.SelectedItem is not SelectorBarItem item) return;
        if ((string)item.Tag == "custom")
        {
            CustomHours.Visibility = Visibility.Visible;
            double v = CustomHours.Value;
            _hours = double.IsNaN(v) ? 12 : Math.Clamp(v, 1, 2160);
        }
        else
        {
            CustomHours.Visibility = Visibility.Collapsed;
            _hours = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        }
        App.Settings.RangeHours = _hours;
        App.Settings.SaveSoon();
        _ = RefreshAsync(force: true);
    }

    private void OnCustomHoursChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // ValueChanged also fires while the XAML is loading; ignore everything before the constructor is done.
        if (_restoring || !_initialized || double.IsNaN(args.NewValue)) return;
        _hours = Math.Clamp(args.NewValue, 1, 2160);
        App.Settings.RangeHours = _hours;
        App.Settings.SaveSoon();
        _ = RefreshAsync(force: true);
    }

    private (long From, long To) Range()
    {
        long to = TimeUtil.NowUnix();
        return (to - (long)(_hours * 3600), to);
    }

    private string RangeLabel => _hours < 1 ? $"{_hours * 60:0} min" : _hours < 48 ? $"{_hours:0.#} h" : $"{_hours / 24:0.#} days";

    // ------------------------------------------------------------------ refresh loop

    private void OnTick()
    {
        if (_closed) return;
        double interval = _hours <= 0.5 ? 3 : _hours <= 6 ? 6 : 15;
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds >= interval) _ = RefreshAsync();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_closed || _busy) { if (force) _lastRefresh = DateTime.MinValue; return; }
        _busy = true;
        try
        {
            var (from, to) = Range();
            var sum = await _client.CallAsync<SummaryResponse>(new SummaryRequest { FromUnix = from, ToUnix = to });
            var ser = await _client.CallAsync<SeriesResponse>(new SeriesRequest { FromUnix = from, ToUnix = to, BucketSeconds = 0 });
            if (_closed) return;
            ApplySummary(sum);
            ApplySeries(ser);

            long wanted = _selected;
            if (wanted != 0 && (force || (DateTime.UtcNow - _lastDetail).TotalSeconds >= 5))
            {
                var d = await _client.CallAsync<AppDetailResponse>(new AppDetailRequest { AppId = wanted, FromUnix = from, ToUnix = to });
                if (!_closed && wanted == _selected)   // the user may have picked another app while we waited
                    _details.Apply(d, App.Live.Info?.RamTotalBytes ?? 0);
                _lastDetail = DateTime.UtcNow;
            }
            _lastRefresh = DateTime.UtcNow;
            UpdateStatus(true);
        }
        catch (CollectorUnavailableException) { UpdateStatus(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally { _busy = false; }
    }

    private void UpdateStatus(bool? reachable = null)
    {
        bool ok = reachable ?? App.Live.Connected;
        var info = App.Live.Info;
        string text = !ok ? "Collector not reachable – retrying"
            : info == null ? "Connected"
            : info.IsElevated ? "Collector running elevated" : "Collector not elevated – network & disk detail limited";
        if (text == StatusText.Text) return;   // called every second; touch the UI only on change
        StatusText.Text = text;
        StatusDot.Fill = Theme.Brush(ok ? Theme.Good : Theme.Warn);
    }

    private void OnLiveUpdated()
    {
        if (_closed) return;
        UpdateStatus();
        var f = App.Live.Latest;
        if (f == null) return;

        var s = f.System;
        _cpu.Now.Text = $"{s.CpuPct:0}%";
        _ram.Now.Text = $"{Format.Bytes(s.RamUsedBytes)} / {Format.Bytes(s.RamTotalBytes, 0)}";
        _gpu.Now.Text = f.GpuAvailable ? $"{s.GpuPct:0}%" : "n/a";
        _net.Now.Text = f.EtwAvailable ? $"↓ {Format.Rate(s.NetRxBps)}   ↑ {Format.Rate(s.NetTxBps)}" : "n/a";
        _disk.Now.Text = $"R {Format.Rate(s.DiskReadBps)}   W {Format.Rate(s.DiskWriteBps)}";
        foreach (var t in new[] { _net.Now, _disk.Now }) t.FontSize = 14;

        var live = f.Apps.ToDictionary(a => a.AppId);
        foreach (var vm in _rows.Values) vm.SetLive(live.TryGetValue(vm.AppId, out var a) ? a : null);
    }

    // ------------------------------------------------------------------ series -> cards

    private void ApplySeries(SeriesResponse ser)
    {
        var p = ser.Points;
        var t = p.Select(x => x.Ts).ToArray();
        double Avg(Func<SeriesPoint, double> f) => p.Count == 0 ? 0 : p.Average(f);
        double Max(Func<SeriesPoint, double> f) => p.Count == 0 ? 0 : p.Max(f);
        double bucket = ser.BucketSeconds > 0 ? ser.BucketSeconds : 1;
        double total = App.Live.Info?.RamTotalBytes ?? 0;

        _cpu.Chart.SetData(t, new[]
        {
            new ChartSeries("Average", Theme.Cpu, p.Select(x => x.Cpu).ToArray()),
            new ChartSeries("Peak", Theme.Cpu, p.Select(x => x.CpuMax).ToArray(), Fill: false),
        }, null, v => $"{v:0.#}%", 10);
        _cpu.Range.Text = $"Last {RangeLabel}: avg {Avg(x => x.Cpu):0.#}% · peak {Max(x => x.CpuMax):0}%";

        _ram.Chart.SetData(t, new[] { new ChartSeries("Used", Theme.Ram, p.Select(x => x.Ram).ToArray()) }, total > 0 ? total : null, v => Format.Bytes(v, 1));
        _ram.Range.Text = $"Last {RangeLabel}: avg {Format.Bytes(Avg(x => x.Ram))} · peak {Format.Bytes(Max(x => x.Ram))}";

        _gpu.Chart.SetData(t, new[]
        {
            new ChartSeries("Average", Theme.Gpu, p.Select(x => x.Gpu).ToArray()),
            new ChartSeries("Peak", Theme.Gpu, p.Select(x => x.GpuMax).ToArray(), Fill: false),
        }, null, v => $"{v:0.#}%", 10);
        _gpu.Range.Text = $"Last {RangeLabel}: avg {Avg(x => x.Gpu):0.#}% · peak {Max(x => x.GpuMax):0}%";

        _net.Chart.SetData(t, new[]
        {
            new ChartSeries("Download", Theme.Net, p.Select(x => x.NetRx).ToArray()),
            new ChartSeries("Upload", Theme.Warn, p.Select(x => x.NetTx).ToArray()),
        }, null, v => Format.Rate(v), 64 * 1024);
        double rx = p.Sum(x => x.NetRx) * bucket, tx = p.Sum(x => x.NetTx) * bucket;
        _net.Range.Text = $"Last {RangeLabel}: ↓ {Format.Bytes(rx)} · ↑ {Format.Bytes(tx)} transferred";

        _disk.Chart.SetData(t, new[]
        {
            new ChartSeries("Read", Theme.Disk, p.Select(x => x.DiskRead).ToArray()),
            new ChartSeries("Write", Windows.UI.Color.FromArgb(0xFF, 0xC0, 0x7A, 0xFF), p.Select(x => x.DiskWrite).ToArray()),
        }, null, v => Format.Rate(v), 256 * 1024);
        double rd = p.Sum(x => x.DiskRead) * bucket, wr = p.Sum(x => x.DiskWrite) * bucket;
        _disk.Range.Text = $"Last {RangeLabel}: {Format.Bytes(rd)} read · {Format.Bytes(wr)} written";
    }

    // ------------------------------------------------------------------ app list

    private static readonly (string Key, string Title, bool Right)[] Columns =
    {
        ("Name", "Application", false), ("NowCpu", "CPU now", true), ("AvgCpu", "CPU avg (max)", true),
        ("AvgRam", "Memory", true), ("AvgGpu", "GPU", true), ("NetBytes", "Network", true),
        ("DiskBytes", "Disk", true), ("Impact", "Impact", true),
    };

    private void BuildListHeader()
    {
        for (int i = 0; i < Columns.Length; i++)
        {
            var (key, title, right) = Columns[i];
            var arrow = new TextBlock { FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9, Margin = new Thickness(4, 0, 0, 0) };
            _headerArrows[key] = arrow;
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.65 });
            content.Children.Add(arrow);
            var b = new Button
            {
                Content = content, Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Padding = new Thickness(i == 0 ? 4 : 0, 2, 14, 2), Margin = new Thickness(i == 0 ? 0 : 0, 0, 0, 0), Tag = key,
            };
            b.Click += (_, _) => SortBy(key);
            Grid.SetColumn(b, i);
            ListHeader.Children.Add(b);
        }
        UpdateHeaderArrows();
    }

    private void SortBy(string key)
    {
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key != "Name"; }
        UpdateHeaderArrows();
        RebuildList();
    }

    private void UpdateHeaderArrows()
    {
        foreach (var (key, arrow) in _headerArrows)
            arrow.Text = key == _sortKey ? (_sortDesc ? "▼" : "▲") : "";
    }

    private void ApplySummary(SummaryResponse sum)
    {
        var live = (App.Live.Latest?.Apps ?? new List<AppUsage>()).GroupBy(a => a.AppId).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<long>();
        foreach (var s in sum.Apps)
        {
            seen.Add(s.AppId);
            if (!_rows.TryGetValue(s.AppId, out var vm)) _rows[s.AppId] = vm = new AppRowVm(s.AppId);
            vm.Update(s, live.TryGetValue(s.AppId, out var a) ? a : null);
        }
        foreach (long gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) _rows.Remove(gone);
        RebuildList();

        int running = sum.Apps.Count(a => live.ContainsKey(a.AppId));
        int flagged = sum.Apps.Count(a => a.Flags != AppFlags.None);
        _summary.Now.Text = $"{sum.Apps.Count}";
        _summary.Range.Text = $"{running} running now · {flagged} flagged\nSeen in the last {RangeLabel}";
    }

    private void RebuildList()
    {
        string q = SearchBox.Text.Trim();
        bool onlyFlagged = SuspiciousToggle.IsChecked == true;
        IEnumerable<AppRowVm> rows = _rows.Values;
        if (onlyFlagged) rows = rows.Where(r => r.Flags != AppFlags.None);
        if (q.Length > 0)
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.ExePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        Func<AppRowVm, object> key = _sortKey switch
        {
            "Name" => r => r.Name.ToLowerInvariant(),
            "NowCpu" => r => r.NowCpu,
            "AvgCpu" => r => r.AvgCpu,
            "AvgRam" => r => r.AvgRam,
            "AvgGpu" => r => r.AvgGpu,
            "NetBytes" => r => r.NetBytes,
            "DiskBytes" => r => r.DiskBytes,
            _ => r => r.Impact,
        };
        var sorted = (_sortDesc ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        EmptyText.Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _rows.Count == 0
            ? "No data yet.\nThe collector records one row per application per minute."
            : "No application matches the current filter.";

        // Same items in the same order: the view models were updated in place, nothing else to do.
        if (sorted.Count == _shown.Count && sorted.Zip(_shown).All(p => p.First.AppId == p.Second.AppId)) return;

        var scroll = FindScrollViewer(AppList);
        double offset = scroll?.VerticalOffset ?? 0;
        _restoring = true;
        try
        {
            _shown = sorted;
            AppList.ItemsSource = sorted;
            var sel = sorted.FirstOrDefault(r => r.AppId == _selected);
            if (sel != null) AppList.SelectedItem = sel;
        }
        finally { _restoring = false; }
        if (scroll != null && offset > 0) { AppList.UpdateLayout(); scroll.ChangeView(null, offset, null, true); }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    private void OnFilterChanged(object sender, object e)
    {
        if (!IsLoaded()) return;
        App.Settings.SuspiciousOnly = SuspiciousToggle.IsChecked == true;
        App.Settings.SaveSoon();
        RebuildList();
    }

    private bool IsLoaded() => Root != null && _shown != null && SearchBox != null && SuspiciousToggle != null;

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring) return;
        if (AppList.SelectedItem is AppRowVm vm)
        {
            _selected = vm.AppId;
            _details.Select(vm);
            _lastDetail = DateTime.MinValue;
            _ = RefreshAsync(force: true);
        }
        else if (_shown.All(r => r.AppId != _selected))
        {
            _selected = 0;
            _details.Clear();
        }
    }
}
