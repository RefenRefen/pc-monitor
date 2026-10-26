using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using PcMonitor.Widget.Services;
using Windows.UI;

namespace PcMonitor.Widget.Controls;

/// <summary>One resource line of the compact widget: label, current value and a 2-minute sparkline.</summary>
public sealed class MetricRow : Grid
{
    private readonly TextBlock _value = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Strong, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 10.5, Foreground = Theme.Faint, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Sparkline _spark;
    private readonly List<double> _scratch = new(LiveService.HistoryLength);

    public MetricRow(string label, Color color)
    {
        Height = 30;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });

        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Background = Theme.Brush(color), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var left = new Grid();
        left.Children.Add(bar);
        left.Children.Add(text);
        SetColumn(left, 0);

        var valueStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        valueStack.Children.Add(_value);
        valueStack.Children.Add(_sub);
        SetColumn(valueStack, 1);

        _spark = new Sparkline(color) { Margin = new Thickness(0, 3, 0, 3) };
        SetColumn(_spark, 2);

        Children.Add(left);
        Children.Add(valueStack);
        Children.Add(_spark);
    }

    public void Update(string value, string? sub, IEnumerable<double> history, double max)
    {
        _value.Text = value;
        _sub.Text = sub ?? "";
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _scratch.Clear();
        _scratch.AddRange(history);
        _spark.SetData(_scratch, max, LiveService.HistoryLength);
    }
}

/// <summary>An application line (icon, name, dominant usage) used in the compact widget's "top apps" list.</summary>
public sealed class CompactAppRow : Grid
{
    private readonly Image _icon = new() { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Strong, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 10.5, Foreground = Theme.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _flag = new() { Glyph = "\uE7BA", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private long _appId;
    private readonly IconCache _icons;

    public CompactAppRow(IconCache icons)
    {
        _icons = icons;
        Height = 40;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Generic glyph shown until (or instead of) a real icon, e.g. kernel pseudo-processes have none.
        var placeholder = new FontIcon { Glyph = "\uE74C", FontSize = 16, Opacity = 0.35, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
        SetColumn(placeholder, 0);
        Children.Add(placeholder);
        SetColumn(_icon, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_name);
        stack.Children.Add(_detail);
        SetColumn(stack, 1);
        SetColumn(_flag, 2);
        Children.Add(_icon);
        Children.Add(stack);
        Children.Add(_flag);
        Opacity = 0;
    }

    public void Update(AppUsage? a)
    {
        // Empty slots stay in the layout (invisible) so the widget never changes height when apps come and go.
        if (a == null) { Opacity = 0; return; }
        Opacity = 1;
        if (_appId != a.AppId)
        {
            _appId = a.AppId;
            _icons.Attach(_icon, a.AppId, a.ExePath);
        }
        _name.Text = a.Name;
        _detail.Text = Summarize(a);

        bool flagged = a.Flags != AppFlags.None && (Theme.IsAlarming(a.Flags) || a.Flags.HasFlag(AppFlags.Unsigned));
        _flag.Visibility = flagged ? Visibility.Visible : Visibility.Collapsed;
        if (flagged)
        {
            _flag.Foreground = Theme.Brush(Theme.IsAlarming(a.Flags) ? Theme.Danger : Theme.Warn);
            ToolTipService.SetToolTip(_flag, Theme.Describe(a.Flags));
        }
    }

    /// <summary>CPU, memory and the busiest I/O source - the three things people look at first.</summary>
    private static string Summarize(AppUsage a)
    {
        var parts = new List<string> { $"CPU {a.CpuPct:0.#}%", Format.Bytes(a.PrivateBytes, 0) };
        if (a.GpuPct >= 1) parts.Add($"GPU {a.GpuPct:0}%");
        long net = a.NetRxBps + a.NetTxBps;
        long disk = a.DiskReadBps + a.DiskWriteBps;
        if (net >= 1024 && net >= disk) parts.Add("Net " + Format.Rate(net, 0));
        else if (disk >= 1024) parts.Add("Disk " + Format.Rate(disk, 0));
        return string.Join("  ·  ", parts);
    }
}
