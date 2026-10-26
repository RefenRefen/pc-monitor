using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.UI;

namespace PcMonitor.Widget.Controls;

/// <summary>Minimal area sparkline made of two vector paths (no bitmap redraws, no third-party chart library).</summary>
public sealed class Sparkline : Grid
{
    private readonly Path _fill = new() { StrokeThickness = 0 };
    private readonly Path _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private double _max = 100;
    private int _slots = 120;

    public Sparkline(Color color)
    {
        _line.Stroke = new SolidColorBrush(color);
        _fill.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x70, color.R, color.G, color.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x00, color.R, color.G, color.B), Offset = 1 },
            },
        };
        Children.Add(_fill);
        Children.Add(_line);
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Values are drawn right-aligned in <paramref name="slots"/> slots, scaled to 0..max.</summary>
    public void SetData(IReadOnlyList<double> values, double max, int slots)
    {
        _values = values;
        _max = max <= 0 ? 1 : max;
        _slots = Math.Max(2, slots);
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _values.Count;
        if (w < 2 || h < 2 || n < 2)
        {
            _line.Data = null;
            _fill.Data = null;
            return;
        }

        double step = w / (_slots - 1);
        double x0 = w - (n - 1) * step;
        var pts = new PointCollection();
        for (int i = 0; i < n; i++)
        {
            double frac = Math.Clamp(_values[i] / _max, 0, 1);
            pts.Add(new Point(x0 + i * step, h - 1.5 - frac * (h - 3)));
        }

        var lineFigure = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        var lineSeg = new PolyLineSegment { Points = pts };
        lineFigure.Segments.Add(lineSeg);
        var lineGeo = new PathGeometry();
        lineGeo.Figures.Add(lineFigure);
        _line.Data = lineGeo;

        var fillPts = new PointCollection();
        foreach (var p in pts) fillPts.Add(p);
        fillPts.Add(new Point(pts[n - 1].X, h));
        fillPts.Add(new Point(pts[0].X, h));
        var fillFigure = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        fillFigure.Segments.Add(new PolyLineSegment { Points = fillPts });
        var fillGeo = new PathGeometry();
        fillGeo.Figures.Add(fillFigure);
        _fill.Data = fillGeo;
    }
}
