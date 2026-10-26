using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PcMonitor.Widget.Native;
using PcMonitor.Widget.Services;

namespace PcMonitor.Widget;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static CollectorClient Client { get; private set; } = null!;
    public static LiveService Live { get; private set; } = null!;
    public static IconCache Icons { get; private set; } = null!;
    public static DispatcherQueue UiQueue { get; private set; } = null!;

    public bool Exiting { get; private set; }

    private CompactWindow? _compact;
    private DashboardWindow? _dashboard;
    private TrayIcon? _tray;
    private ContextReporter? _reporter;

    private const int CmdToggleWidget = 1, CmdDashboard = 2, CmdTop = 3, CmdExit = 4;

    public App()
    {
        RequestedTheme = ApplicationTheme.Dark;   // the glass palette is designed for dark; must be set before InitializeComponent
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A monitoring widget must not die because of a rendering hiccup.
            e.Handled = true;
            try { File.AppendAllText(Path.Combine(AppSettings.Folder, "widget-errors.log"), $"{DateTime.Now:s} {e.Exception}\n"); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Instance = this;
        UiQueue = DispatcherQueue.GetForCurrentThread();
        Directory.CreateDirectory(AppSettings.Folder);
        Settings = AppSettings.Load();
        Client = new CollectorClient();
        Live = new LiveService(UiQueue, Client);
        Icons = new IconCache(UiQueue);

        _compact = new CompactWindow();
        _reporter = new ContextReporter(Client);
        _tray = new TrayIcon("PC Monitor", BuildTrayMenu);
        _tray.Activated += ToggleCompact;
        _tray.MenuCommand += OnTrayCommand;

        // A second launch (Start menu, installer "run now") redirects here and opens the dashboard.
        AppInstance.GetCurrent().Activated += (_, _) => UiQueue.TryEnqueue(ShowDashboard);

        string[] cmd = Environment.GetCommandLineArgs();
        bool startHidden = cmd.Contains("--hidden");
        if (cmd.Contains("--dashboard")) ShowDashboard();
        if (Settings.CompactVisible && !startHidden) ShowCompact();
        ScheduleTrim();
    }

    // ------------------------------------------------------------------ compact widget

    public void ShowCompact()
    {
        _compact?.ShowWidget();
        Live.SetDemand(LiveDemand.Compact, true);
        Settings.CompactVisible = true;
        Settings.SaveSoon();
    }

    public void HideCompact()
    {
        _compact?.HideWidget();
        Live.SetDemand(LiveDemand.Compact, false);
        Settings.CompactVisible = false;
        Settings.SaveSoon();
        ScheduleTrim();
    }

    private DispatcherQueueTimer? _trimTimer;

    /// <summary>Shortly after the UI settles (start-up, widget hidden, dashboard closed) give unused memory back to Windows.</summary>
    private void ScheduleTrim()
    {
        _trimTimer ??= UiQueue.CreateTimer();
        _trimTimer.Stop();
        _trimTimer.Interval = TimeSpan.FromSeconds(20);
        _trimTimer.IsRepeating = false;
        _trimTimer.Tick -= OnTrimTick;
        _trimTimer.Tick += OnTrimTick;
        _trimTimer.Start();
    }

    private void OnTrimTick(DispatcherQueueTimer sender, object args) => MemoryTrim.Run();

    public void ToggleCompact()
    {
        if (_compact is { IsShown: true }) HideCompact(); else ShowCompact();
    }

    public void SetAlwaysOnTop(bool on)
    {
        Settings.AlwaysOnTop = on;
        Settings.SaveSoon();
        _compact?.SetAlwaysOnTop(on);
    }

    // ------------------------------------------------------------------ dashboard

    public void ShowDashboard()
    {
        if (_dashboard == null)
        {
            _dashboard = new DashboardWindow();
            _dashboard.Closed += (_, _) =>
            {
                // Free the whole visual tree while only the pill (or nothing) is on screen.
                _dashboard = null;
                Live.SetDemand(LiveDemand.Dashboard, false);
                ScheduleTrim();
            };
            Live.SetDemand(LiveDemand.Dashboard, true);
        }
        _dashboard.ShowAndActivate();
    }

    // ------------------------------------------------------------------ tray

    private IReadOnlyList<TrayIcon.MenuItem> BuildTrayMenu() => new[]
    {
        new TrayIcon.MenuItem(CmdToggleWidget, _compact is { IsShown: true } ? "Hide widget" : "Show widget"),
        new TrayIcon.MenuItem(CmdDashboard, "Open dashboard"),
        new TrayIcon.MenuItem(0, "", Separator: true),
        new TrayIcon.MenuItem(CmdTop, "Always on top", Settings.AlwaysOnTop),
        new TrayIcon.MenuItem(0, "", Separator: true),
        new TrayIcon.MenuItem(CmdExit, "Exit PC Monitor"),
    };

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case CmdToggleWidget: ToggleCompact(); break;
            case CmdDashboard: ShowDashboard(); break;
            case CmdTop: SetAlwaysOnTop(!Settings.AlwaysOnTop); break;
            case CmdExit: ExitApp(); break;
        }
    }

    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        Settings.SaveNow();
        _tray?.Dispose();
        _reporter?.Dispose();
        Live.SetDemand(LiveDemand.Compact | LiveDemand.Dashboard, false);
        _dashboard?.Close();
        _compact?.Close();
        Client.Dispose();
        Exit();
    }
}
