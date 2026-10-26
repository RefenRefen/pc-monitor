using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Line = Microsoft.UI.Xaml.Shapes.Line;

namespace PcMonitor.Widget.Controls;

public sealed record ChartSeries(string Name, Color Color, double[] Values, bool Fill = true);

/// <summary>
/// Time-series chart drawn from vector paths: filled areas / lines, light grid with value labels,
/// time labels, and a hover read-out. Gaps in the data (collector stopped) are drawn as gaps, not interpolated.
/// </summary>
public sealed class AreaChart : Grid
{
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)) };
    private readonly Grid _timeLabels = new() { Height = 16, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _t0 = new(), _t1 = new(), _t2 = new();
    private readonly Line _hoverLine = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Border _tip = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x22, 0x2A)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 6),
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private readonly TextBlock _tipText = new() { FontSize = 11.5, LineHeight = 16 };

    private long[] _times = Array.Empty<long>();
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private double? _fixedMax;
    private double _max = 1;
    private Func<double, string> _fmt = v => v.ToString("0.#");
    private double _gapSeconds = double.MaxValue;
    private bool _dateLabels;

    public AreaChart()
    {
        MinHeight = 70;
        Children.Add(_canvas);
        _timeLabels.ColumnDefinitions.Add(new ColumnDefinition());
        _timeLabels.ColumnDefinitions.Add(new ColumnDefinition());
        _timeLabels.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (tb, col, align) in new[] { (_t0, 0, HorizontalAlignment.Left), (_t1, 1, HorizontalAlignment.Center), (_t2, 2, HorizontalAlignment.Right) })
        {
            tb.FontSize = 10; tb.Foreground = Theme.Faint; tb.HorizontalAlignment = align;
            SetColumn(tb, col);
            _timeLabels.Children.Add(tb);
        }
        _timeLabels.IsHitTestVisible = false;
        Children.Add(_timeLabels);
        _tip.Child = _tipText;
        _canvas.Children.Add(_hoverLine);
        _canvas.Children.Add(_tip);

        SizeChanged += (_, _) => Redraw();
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += (_, _) => { _hoverLine.Visibility = Visibility.Collapsed; _tip.Visibility = Visibility.Collapsed; };
    }

    public void SetData(long[] times, IReadOnlyList<ChartSeries> series, double? fixedMax, Func<double, string> format, double minMax = 0)
    {
        _times = times;
        _series = series;
        _fixedMax = fixedMax;
        _fmt = format;

        double peak = 0;
        foreach (var s in series) foreach (var v in s.Values) if (v > peak) peak = v;
        _max = fixedMax ?? Math.Max(minMax, NiceCeiling(peak * 1.08));
        if (_max <= 0) _max = 1;

        _gapSeconds = double.MaxValue;
        if (times.Length > 2)
        {
            var steps = new List<long>(times.Length - 1);
            for (int i = 1; i < times.Length; i++) steps.Add(times[i] - times[i - 1]);
            steps.Sort();
            _gapSeconds = Math.Max(1, steps[steps.Count / 2]) * 2.5;
        }
        _dateLabels = times.Length > 1 && times[^1] - times[0] > 36 * 3600;
        Redraw();
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 7.5, 10 })
            if (v <= m * mag) return m * mag;
        return 10 * mag;
    }

    private double PlotHeight => Math.Max(10, ActualHeight - 18);

    private double XOf(long t)
    {
        long t0 = _times[0], t1 = _times[^1];
        return t1 == t0 ? ActualWidth / 2 : (t - t0) / (double)(t1 - t0) * ActualWidth;
    }

    private double YOf(double v) => PlotHeight - Math.Clamp(v / _max, 0, 1) * (PlotHeight - 4) - 1;

    private void Redraw()
    {
        // Keep the hover elements, rebuild everything else.
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            var c = _canvas.Children[i];
            if (c != _hoverLine && c != _tip) _canvas.Children.RemoveAt(i);
        }
        double w = ActualWidth, h = PlotHeight;
        if (w < 10 || h < 10) return;

        // grid lines at 0 / 50 / 100 % of the axis maximum, labelled in their own unit
        for (int i = 0; i <= 2; i++)
        {
            double y = YOf(_max * i / 2.0);
            var ln = new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)0x30 : (byte)0x16, 255, 255, 255)), StrokeThickness = 1 };
            _canvas.Children.Insert(0, ln);
            if (i > 0)
            {
                var lbl = new TextBlock { Text = _fmt(_max * i / 2.0), FontSize = 10, Foreground = Theme.Faint, IsHitTestVisible = false };
                Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, Math.Max(0, y - 15));
                _canvas.Children.Add(lbl);
            }
        }

        if (_times.Length == 0 || _series.Count == 0)
        {
            var none = new TextBlock { Text = "No data in this range", FontSize = 12, Foreground = Theme.Faint, IsHitTestVisible = false };
            Canvas.SetLeft(none, w / 2 - 55); Canvas.SetTop(none, h / 2 - 8);
            _canvas.Children.Add(none);
            _t0.Text = _t1.Text = _t2.Text = "";
            return;
        }

        foreach (var s in _series) AddSeries(s, w, h);
        _hoverLine.Y1 = 0; _hoverLine.Y2 = h;
        Canvas.SetZIndex(_hoverLine, 10);
        Canvas.SetZIndex(_tip, 11);

        long a = _times[0], b = _times[^1];
        _t0.Text = Stamp(a); _t1.Text = Stamp(a + (b - a) / 2); _t2.Text = Stamp(b);
    }

    private string Stamp(long unix)
    {
        var dt = TimeUtil.FromUnixLocal(unix);
        return _dateLabels ? dt.ToString("MMM d HH:mm") : dt.ToString("HH:mm");
    }

    private void AddSeries(ChartSeries s, double w, double h)
    {
        var lineGeo = new PathGeometry();
        var fillGeo = new PathGeometry();
        int n = Math.Min(_times.Length, s.Values.Length);
        int start = 0;
        while (start < n)
        {
            int end = start;
            while (end + 1 < n && _times[end + 1] - _times[end] <= _gapSeconds) end++;

            var pts = new PointCollection();
            for (int i = start; i <= end; i++) pts.Add(new Point(XOf(_times[i]), YOf(s.Values[i])));
            if (pts.Count == 1)   // a lone sample: draw a short tick so it is visible
            {
                var p = pts[0];
                pts = new PointCollection { new Point(p.X - 1.5, p.Y), new Point(p.X + 1.5, p.Y) };
            }

            var lf = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
            lf.Segments.Add(new PolyLineSegment { Points = pts });
            lineGeo.Figures.Add(lf);

            if (s.Fill)
            {
                var fp = new PointCollection();
                foreach (var p in pts) fp.Add(p);
                fp.Add(new Point(pts[^1].X, h));
                fp.Add(new Point(pts[0].X, h));
                var ff = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
                ff.Segments.Add(new PolyLineSegment { Points = fp });
                fillGeo.Figures.Add(ff);
            }
            start = end + 1;
        }

        if (s.Fill)
        {
            _canvas.Children.Add(new Path
            {
                Data = fillGeo, IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0x66, s.Color.R, s.Color.G, s.Color.B), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0x08, s.Color.R, s.Color.G, s.Color.B), Offset = 1 },
                    },
                },
            });
        }
        _canvas.Children.Add(new Path
        {
            Data = lineGeo, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(s.Fill ? s.Color : Color.FromArgb(0xAA, s.Color.R, s.Color.G, s.Color.B)),
            StrokeThickness = s.Fill ? 1.6 : 1.2,
            StrokeDashArray = s.Fill ? null : new DoubleCollection { 3, 2 },
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_times.Length == 0 || ActualWidth < 10) return;
        double x = e.GetCurrentPoint(_canvas).Position.X;

        int lo = 0, hi = _times.Length - 1;   // nearest sample by x
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (XOf(_times[mid]) < x) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(XOf(_times[lo - 1]) - x) < Math.Abs(XOf(_times[lo]) - x)) lo--;

        double px = XOf(_times[lo]);
        _hoverLine.X1 = _hoverLine.X2 = px;
        _hoverLine.Visibility = Visibility.Visible;

        var lines = new List<string> { TimeUtil.FromUnixLocal(_times[lo]).ToString("ddd MMM d  HH:mm") };
        foreach (var s in _series)
            if (lo < s.Values.Length) lines.Add($"{s.Name}: {_fmt(s.Values[lo])}");
        _tipText.Text = string.Join("\n", lines);
        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = px + 12;
        if (left + _tip.DesiredSize.Width > ActualWidth) left = px - 12 - _tip.DesiredSize.Width;
        Canvas.SetLeft(_tip, Math.Max(0, left));
        Canvas.SetTop(_tip, 4);
    }
}
