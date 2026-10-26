using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using PcMonitor.Widget.Controls;

namespace PcMonitor.Widget.ViewModels;

/// <summary>One row of the dashboard application list. Updated in place so the list does not flicker.</summary>
public sealed class AppRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public long AppId { get; }
    public string Name { get; private set; } = "";
    public string? ExePath { get; private set; }
    public string? Publisher { get; private set; }
    public AppFlags Flags { get; private set; }
    public SignatureState Signature { get; private set; }

    // Raw values, used for sorting.
    public double NowCpu { get; private set; }
    public long NowRam { get; private set; }
    public bool Running { get; private set; }
    public double AvgCpu { get; private set; }
    public double AvgRam { get; private set; }
    public double AvgGpu { get; private set; }
    public long NetBytes { get; private set; }
    public long DiskBytes { get; private set; }
    public double Impact { get; private set; }
    public long LastSeen { get; private set; }

    // Display strings.
    public string NowCpuText => Running ? $"{NowCpu:0.#}%" : "–";
    public string AvgCpuText => $"{AvgCpu:0.#}%  (max {MaxCpu:0}%)";
    public double MaxCpu { get; private set; }
    public string RamText => AvgRam > 0 ? Format.Bytes(AvgRam, 0) : "–";
    public string GpuText => AvgGpu >= 0.05 ? $"{AvgGpu:0.#}%" : "–";
    public string NetText => NetBytes > 0 ? Format.Bytes(NetBytes) : "–";
    public string DiskText => DiskBytes > 0 ? Format.Bytes(DiskBytes) : "–";
    public string Subtitle => string.IsNullOrWhiteSpace(Publisher)
        ? (Signature == SignatureState.Unsigned ? "Unsigned" : "")
        : Publisher!;
    public string StatusText => Running ? "running" : "last seen " + TimeUtil.FromUnixLocal(LastSeen).ToString("g");

    public Visibility FlagVisibility => Flags != AppFlags.None ? Visibility.Visible : Visibility.Collapsed;
    public Brush FlagBrush => Theme.Brush(Theme.IsAlarming(Flags) ? Theme.Danger : Theme.Warn);
    public string FlagTip => Theme.Describe(Flags);
    public Brush RunningBrush => Theme.Brush(Running ? Theme.Good : Color(0x55, 0xFF, 0xFF, 0xFF));

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; private set { _icon = value; Raise(nameof(Icon)); } }

    public AppRowVm(long appId)
    {
        AppId = appId;
    }

    /// <summary>Cheap once-per-second refresh of the "now" column only.</summary>
    public void SetLive(AppUsage? live)
    {
        bool was = Running;
        string oldText = NowCpuText;
        Running = live != null;
        NowCpu = live?.CpuPct ?? 0;
        NowRam = live?.PrivateBytes ?? 0;
        // Only notify when the visible text really changed: most rows are idle and a notification forces a layout pass.
        if (NowCpuText != oldText) Raise(nameof(NowCpuText));
        if (was != Running) { Raise(nameof(RunningBrush)); Raise(nameof(StatusText)); }
    }

    public void Update(AppSummary s, AppUsage? live)
    {
        bool first = Name.Length == 0;
        Name = s.Name;
        ExePath = s.ExePath;
        Publisher = s.Publisher;
        Flags = live != null ? live.Flags | s.Flags : s.Flags;
        Signature = s.Signature;
        AvgCpu = s.AvgCpu; MaxCpu = s.MaxCpu; AvgRam = s.AvgRam; AvgGpu = s.AvgGpu;
        NetBytes = s.NetRxBytes + s.NetTxBytes;
        DiskBytes = s.DiskReadBytes + s.DiskWriteBytes;
        Impact = live?.Impact > 0 ? Math.Max(s.Impact, live.Impact) : s.Impact;
        LastSeen = s.LastSeenUnix;
        Running = live != null;
        NowCpu = live?.CpuPct ?? 0;
        NowRam = live?.PrivateBytes ?? 0;

        Raise(string.Empty); // every property changed
        if (first) App.Icons.Request(AppId, ExePath, null, src => Icon = src);
    }
}
