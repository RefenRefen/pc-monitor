using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using PcMonitor.Widget.Services;
using PcMonitor.Widget.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PcMonitor.Widget.Controls;

/// <summary>
/// "Who is this and what is it doing": identity, why it starts with Windows, resource history, processes,
/// network peers and the actions to deal with it. Static parts are built once per app; live parts refresh in place.
/// </summary>
public sealed class AppDetailsPanel : Grid
{
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _root = new() { Spacing = 14 };
    private readonly TextBlock _hint = new()
    {
        Text = "Select an application to see where it lives, what it is, and what it has been doing.",
        TextWrapping = TextWrapping.Wrap, Foreground = Theme.Faint, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 280,
    };

    private readonly StackPanel _identityHost = new() { Spacing = 14 };
    private readonly StackPanel _startupHost = new() { Spacing = 6 };
    private readonly StackPanel _chartsHost = new() { Spacing = 10 };
    private readonly StackPanel _processHost = new() { Spacing = 2 };
    private readonly StackPanel _connectionHost = new() { Spacing = 2 };
    private readonly StackPanel _messageHost = new();

    private AppRowVm? _app;
    private AppIdentity? _identity;
    private string _identityKey = "";
    private readonly AreaChart _cpuChart = new() { Height = 92 };
    private readonly AreaChart _ramChart = new() { Height = 92 };
    private readonly AreaChart _netChart = new() { Height = 92 };
    private readonly AreaChart _diskChart = new() { Height = 92 };
    private readonly AreaChart _gpuChart = new() { Height = 92 };

    /// <summary>Raised after a command changed something so the owner can refresh its data.</summary>
    public event Action? DataChanged;

    public AppDetailsPanel()
    {
        _root.Children.Add(_messageHost);
        _root.Children.Add(_identityHost);
        _root.Children.Add(Section("Start-up entries  (why does it run?)", _startupHost));
        _root.Children.Add(Section("Usage in selected range", _chartsHost));
        _root.Children.Add(Section("Running processes", _processHost));
        _root.Children.Add(Section("Network peers", _connectionHost));
        _scroll.Content = _root;
        Children.Add(_scroll);
        Children.Add(_hint);

        _chartsHost.Children.Add(Labeled("CPU", _cpuChart));
        _chartsHost.Children.Add(Labeled("Memory (private working set)", _ramChart));
        _chartsHost.Children.Add(Labeled("GPU", _gpuChart));
        _chartsHost.Children.Add(Labeled("Network", _netChart));
        _chartsHost.Children.Add(Labeled("Disk", _diskChart));
        Clear();
    }

    public long? SelectedAppId => _app?.AppId;

    public void Clear()
    {
        _app = null;
        _identity = null;
        _identityKey = "";
        _scroll.Visibility = Visibility.Collapsed;
        _hint.Visibility = Visibility.Visible;
    }

    public void Select(AppRowVm app)
    {
        if (_app?.AppId != app.AppId)
        {
            _identity = null;
            _identityKey = "";
            _identityHost.Children.Clear();
            _startupHost.Children.Clear();
            _processHost.Children.Clear();
            _connectionHost.Children.Clear();
            _messageHost.Children.Clear();
            _scroll.ChangeView(0, 0, null, true);
        }
        _app = app;
        _scroll.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ data

    public void Apply(AppDetailResponse d, double ramTotalBytes)
    {
        if (_app == null) return;

        if (d.Identity != null)
        {
            string key = IdentityKey(d.Identity);
            if (key != _identityKey)
            {
                _identityKey = key;
                _identity = d.Identity;
                BuildIdentity(d.Identity, d.Live);
                BuildStartup(d.Identity);
            }
            else
            {
                _identity = d.Identity;
            }
        }

        ApplyCharts(d.Series, ramTotalBytes);
        BuildProcesses(d.Processes);
        BuildConnections(d.Connections);
    }

    private static string IdentityKey(AppIdentity i) =>
        $"{i.AppId}|{i.Complete}|{i.Sha256}|{i.Signature}|{i.StaticFlags}|{i.FileExists}|{string.Join(';', i.Startup.Select(s => s.Name + s.Enabled))}";

    private void ApplyCharts(SeriesResponse series, double ramTotal)
    {
        var p = series.Points;
        var t = p.Select(x => x.Ts).ToArray();
        _cpuChart.SetData(t, new[]
        {
            new ChartSeries("CPU avg", Theme.Cpu, p.Select(x => x.Cpu).ToArray()),
            new ChartSeries("CPU peak", Theme.Cpu, p.Select(x => x.CpuMax).ToArray(), Fill: false),
        }, null, v => $"{v:0.#}%");
        _ramChart.SetData(t, new[] { new ChartSeries("Memory", Theme.Ram, p.Select(x => x.Ram).ToArray()) }, null, v => Format.Bytes(v));
        _gpuChart.SetData(t, new[]
        {
            new ChartSeries("GPU avg", Theme.Gpu, p.Select(x => x.Gpu).ToArray()),
            new ChartSeries("GPU peak", Theme.Gpu, p.Select(x => x.GpuMax).ToArray(), Fill: false),
        }, null, v => $"{v:0.#}%");
        _netChart.SetData(t, new[]
        {
            new ChartSeries("Download", Theme.Net, p.Select(x => x.NetRx).ToArray()),
            new ChartSeries("Upload", Theme.Warn, p.Select(x => x.NetTx).ToArray()),
        }, null, v => Format.Rate(v));
        _diskChart.SetData(t, new[]
        {
            new ChartSeries("Read", Theme.Disk, p.Select(x => x.DiskRead).ToArray()),
            new ChartSeries("Write", Color(0xFF, 0xC0, 0x7A, 0xFF), p.Select(x => x.DiskWrite).ToArray()),
        }, null, v => Format.Rate(v));
    }

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    // ------------------------------------------------------------------ identity

    private void BuildIdentity(AppIdentity id, AppUsage? live)
    {
        _identityHost.Children.Clear();

        // header
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image { Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Top };
        App.Icons.Attach(icon, id.AppId, id.ExePath, id.PackageLogoPath);
        SetColumn(icon, 0);
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = id.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        string sub = new[] { id.Company ?? id.InstallPublisher, id.Version }.Where(s => !string.IsNullOrWhiteSpace(s)).Aggregate("", (a, s) => a.Length == 0 ? s! : a + "  ·  " + s);
        if (sub.Length > 0) titles.Children.Add(new TextBlock { Text = sub, Foreground = Theme.Faint, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(id.Description) && id.Description != id.Name)
            titles.Children.Add(new TextBlock { Text = id.Description, Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        SetColumn(titles, 1);
        header.Children.Add(icon);
        header.Children.Add(titles);
        _identityHost.Children.Add(header);

        // signature + suspicion
        var flags = id.StaticFlags | (live?.Flags ?? AppFlags.None);
        _identityHost.Children.Add(SignatureBadge(id));
        if (flags != AppFlags.None)
        {
            _identityHost.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false,
                Severity = Theme.IsAlarming(flags) ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                Title = Theme.IsAlarming(flags) ? "Worth a closer look" : "Heads up",
                Message = Capitalize(Theme.Describe(flags)) + ".",
            });
        }
        if (!id.FileExists)
            _identityHost.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "The executable no longer exists on disk", Message = "It may have been deleted or moved after it started." });

        // actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(ActionButton("\uE838", "Location", async () => OpenLocation(id.ExePath), id.FileExists, "Show the file in Explorer"));
        actions.Children.Add(ActionButton("\uE774", "VirusTotal", async () => OpenVirusTotal(id), !string.IsNullOrEmpty(id.Sha256), "Opens the hash lookup page. Your file is not uploaded."));
        actions.Children.Add(ActionButton("\uE8C8", "Copy hash", async () => Copy(id.Sha256 ?? ""), !string.IsNullOrEmpty(id.Sha256), "Copy the SHA-256 to the clipboard"));
        var end = ActionButton("\uE711", "End app", EndAppAsync, true, "End every running process of this application");
        end.Foreground = Theme.Brush(Theme.Danger);
        actions.Children.Add(end);
        _identityHost.Children.Add(actions);

        // facts
        var facts = new Grid { ColumnSpacing = 12, RowSpacing = 5 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Theme.Faint, FontSize = 12 };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
            SetRow(l, row); SetRow(v, row); SetColumn(v, 1);
            facts.Children.Add(l); facts.Children.Add(v);
        }

        Fact("Location", id.ExePath);
        Fact("Installed by", id.InstallName != null ? $"{id.InstallName}{(id.InstallPublisher != null ? " (" + id.InstallPublisher + ")" : "")}" : (id.PackageFullName != null ? "Microsoft Store / MSIX package" : "Not found in installed programs (portable or unmanaged)"));
        Fact("Install folder", id.InstallLocation);
        Fact("Install date", id.InstallDate);
        Fact("Package", id.PackageFullName);
        Fact("Uninstall", id.UninstallString);
        Fact("Product", id.ProductName);
        Fact("File size", id.FileSize > 0 ? Format.Bytes(id.FileSize) : null);
        Fact("SHA-256", id.Sha256 ?? (id.Complete ? "(file too large or unreadable)" : "calculating…"));
        Fact("First seen", id.FirstSeenUnix > 0 ? TimeUtil.FromUnixLocal(id.FirstSeenUnix).ToString("g") : null);
        Fact("Last seen", id.LastSeenUnix > 0 ? TimeUtil.FromUnixLocal(id.LastSeenUnix).ToString("g") : null);
        _identityHost.Children.Add(facts);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static UIElement SignatureBadge(AppIdentity id)
    {
        string text; Windows.UI.Color color; string glyph;
        switch (id.Signature)
        {
            case SignatureState.Signed: text = "Signed by " + (id.Signer ?? "unknown publisher"); color = Theme.Good; glyph = "\uE73E"; break;
            case SignatureState.Invalid: text = "Signature present but INVALID" + (id.Signer != null ? " (" + id.Signer + ")" : ""); color = Theme.Danger; glyph = "\uE783"; break;
            case SignatureState.Unsigned: text = "Not digitally signed"; color = Theme.Warn; glyph = "\uE7BA"; break;
            default: text = id.Complete ? "Signature unknown" : "Checking signature…"; color = Windows.UI.Color.FromArgb(0x99, 255, 255, 255); glyph = "\uE9CE"; break;
        }
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Theme.Brush(color) });
        sp.Children.Add(new TextBlock { Text = text, Foreground = Theme.Brush(color), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    // ------------------------------------------------------------------ start-up

    private void BuildStartup(AppIdentity id)
    {
        _startupHost.Children.Clear();
        if (id.Startup.Count == 0)
        {
            _startupHost.Children.Add(new TextBlock { Text = "Not registered to start automatically. It was started by you, by another program, or by a service it belongs to.", Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var e in id.Startup)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{KindText(e.Kind)}  ·  {e.Scope}", FontSize = 11, Foreground = Theme.Faint });
            text.Children.Add(new TextBlock { Text = e.Location, FontSize = 11, Foreground = Theme.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTipService.SetToolTip(text, $"{e.Location}\n{e.Command}");
            row.Children.Add(text);

            if (e.CanDisable)
            {
                var toggle = new ToggleSwitch { IsOn = e.Enabled, OnContent = "On", OffContent = "Off", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                bool ignore = false;
                toggle.Toggled += async (_, _) =>
                {
                    if (ignore) return;
                    toggle.IsEnabled = false;
                    bool want = toggle.IsOn;
                    var ok = await RunCommandAsync(new CommandRequest { Command = want ? CommandKind.EnableStartup : CommandKind.DisableStartup, AppId = id.AppId, Startup = e });
                    if (!ok) { ignore = true; toggle.IsOn = !want; ignore = false; }
                    else e.Enabled = want;
                    toggle.IsEnabled = true;
                };
                SetColumn(toggle, 1);
                row.Children.Add(toggle);
            }
            else
            {
                var note = new TextBlock { Text = e.Enabled ? "enabled" : "disabled", Foreground = Theme.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(note, "Manage this one in Task Scheduler or Services.");
                SetColumn(note, 1);
                row.Children.Add(note);
            }
            _startupHost.Children.Add(row);
        }
    }

    private static string KindText(StartupKind k) => k switch
    {
        StartupKind.RunKey => "Registry Run",
        StartupKind.RunOnceKey => "Registry RunOnce",
        StartupKind.StartupFolder => "Startup folder",
        StartupKind.ScheduledTask => "Scheduled task",
        StartupKind.Service => "Windows service",
        StartupKind.Driver => "Driver",
        _ => k.ToString(),
    };

    // ------------------------------------------------------------------ processes / connections

    private void BuildProcesses(List<ProcessUsage> processes)
    {
        _processHost.Children.Clear();
        if (processes.Count == 0)
        {
            _processHost.Children.Add(new TextBlock { Text = "Not running right now.", Foreground = Theme.Faint, FontSize = 12 });
            return;
        }
        foreach (var p in processes.OrderByDescending(x => x.CpuPct).ThenByDescending(x => x.PrivateBytes).Take(60))
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = $"{p.Name}  ·  PID {p.Pid}" + (p.HasVisibleWindow ? "" : "  ·  no window"), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 });
            string detail = p.Label ?? p.CommandLine ?? "";
            if (detail.Length > 0)
            {
                var d = new TextBlock { Text = detail, FontSize = 10.5, Foreground = Theme.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(d, detail);
                name.Children.Add(d);
            }
            row.Children.Add(name);

            var usage = new TextBlock { Text = $"CPU {p.CpuPct:0.#}%\n{Format.Bytes(p.PrivateBytes, 0)}", FontSize = 11, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            SetColumn(usage, 1);
            row.Children.Add(usage);

            int pid = p.Pid;
            var end = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }, Padding = new Thickness(7, 5, 7, 5), Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
            ToolTipService.SetToolTip(end, "End this process");
            end.Click += async (_, _) =>
            {
                if (await ConfirmAsync("End process?", $"End {p.Name} (PID {pid})? Unsaved data in it will be lost.", "End process"))
                    await RunCommandAsync(new CommandRequest { Command = CommandKind.EndProcess, Pid = pid });
            };
            SetColumn(end, 2);
            row.Children.Add(end);
            _processHost.Children.Add(row);
        }
    }

    private void BuildConnections(List<ConnectionInfo> conns)
    {
        _connectionHost.Children.Clear();
        if (conns.Count == 0)
        {
            _connectionHost.Children.Add(new TextBlock
            {
                Text = App.Live.Info is { EtwAvailable: false } ? "Network peers are only recorded while the collector runs elevated." : "No network traffic recorded in this range.",
                Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var c in conns.OrderByDescending(x => x.LastSeenUnix).Take(80))
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(c.RemoteHost) ? c.RemoteIp : c.RemoteHost, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
            left.Children.Add(new TextBlock { Text = $"{c.RemoteIp}:{c.Port}  ·  {c.Proto.ToUpperInvariant()}  ·  last {TimeUtil.FromUnixLocal(c.LastSeenUnix):g}", FontSize = 10.5, Foreground = Theme.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
            var bytes = new TextBlock { Text = Format.Bytes(c.Bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            SetColumn(bytes, 1);
            row.Children.Add(left); row.Children.Add(bytes);
            _connectionHost.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ actions

    private static void OpenLocation(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* explorer unavailable */ }
    }

    private static void OpenVirusTotal(AppIdentity id)
    {
        if (string.IsNullOrEmpty(id.Sha256)) return;
        try { Process.Start(new ProcessStartInfo("https://www.virustotal.com/gui/file/" + id.Sha256) { UseShellExecute = true }); } catch { }
    }

    private static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private async Task EndAppAsync()
    {
        if (_app == null) return;
        if (await ConfirmAsync("End application?", $"End every running process of {_app.Name}? Unsaved data in it will be lost.", "End app"))
            await RunCommandAsync(new CommandRequest { Command = CommandKind.EndApp, AppId = _app.AppId });
    }

    private async Task<bool> RunCommandAsync(CommandRequest request)
    {
        try
        {
            var r = await App.Client.CallAsync<CommandResponse>(request);
            ShowMessage(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, r.Message);
            if (r.Success) DataChanged?.Invoke();
            return r.Success;
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, ex.Message);
            return false;
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string text)
    {
        _messageHost.Children.Clear();
        var bar = new InfoBar { IsOpen = true, Severity = severity, Message = text, IsClosable = true };
        _messageHost.Children.Add(bar);
        var timer = App.UiQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { bar.IsOpen = false; _messageHost.Children.Remove(bar); };
        timer.Start();
    }

    private async Task<bool> ConfirmAsync(string title, string text, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ------------------------------------------------------------------ small builders

    private static Button ActionButton(string glyph, string text, Func<Task> click, bool enabled, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var b = new Button { Content = content, IsEnabled = enabled, Padding = new Thickness(8, 6, 8, 6) };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static UIElement Section(string title, UIElement content)
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint, CharacterSpacing = 40 });
        sp.Children.Add(new Border { Height = 1, Background = Theme.Hairline });
        sp.Children.Add(content);
        return sp;
    }

    private static UIElement Labeled(string label, UIElement chart)
    {
        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Theme.Faint });
        sp.Children.Add(chart);
        return sp;
    }
}
