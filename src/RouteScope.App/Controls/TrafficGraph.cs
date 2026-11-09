using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace RouteScope.App.Controls;

/// <summary>
/// Smooth area chart used for the live up/down graph. Deliberately dependency-free:
/// it draws with a DrawingContext so it stays crisp at any DPI and costs almost nothing.
/// </summary>
public sealed class TrafficGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentSecondaryProperty = DependencyProperty.Register(
        nameof(AccentSecondary), typeof(Brush), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBaselineProperty = DependencyProperty.Register(
        nameof(ShowBaseline), typeof(bool), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush), typeof(Brush), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush AccentSecondary
    {
        get => (Brush)GetValue(AccentSecondaryProperty);
        set => SetValue(AccentSecondaryProperty, value);
    }

    public bool ShowBaseline
    {
        get => (bool)GetValue(ShowBaselineProperty);
        set => SetValue(ShowBaselineProperty, value);
    }

    public Brush BaselineBrush
    {
        get => (Brush)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var pts = new List<double>();
        if (Values is not null)
        {
            foreach (var o in Values)
            {
                if (o is null) continue;
                pts.Add(Convert.ToDouble(o));
            }
        }

        if (ShowBaseline)
        {
            var baselinePen = new Pen(BaselineBrush, 1);
            for (var i = 1; i < 4; i++)
            {
                var y = h * i / 4.0;
                dc.DrawLine(baselinePen, new Point(0, y), new Point(w, y));
            }
        }

        if (pts.Count < 2) return;

        var max = pts.Max();
        if (max <= 0) max = 1;

        const int samples = 48;
        var step = w / (samples - 1);
        var points = new Point[samples];
        var start = Math.Max(0, pts.Count - samples);
        var count = pts.Count - start;

        for (var i = 0; i < samples; i++)
        {
            var idx = start + (int)Math.Round((double)i / (samples - 1) * (count - 1));
            idx = Math.Clamp(idx, 0, pts.Count - 1);
            var value = pts[idx] / max;
            // Ease so small traffic still shows a little life.
            var eased = Math.Pow(Math.Clamp(value, 0, 1), 0.62);
            points[i] = new Point(i * step, h - 2 - eased * (h - 6));
        }

        var line = BuildSmoothGeometry(points, closeToBottom: false, height: h);
        var area = BuildSmoothGeometry(points, closeToBottom: true, height: h);

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        var accent = Accent as SolidColorBrush ?? new SolidColorBrush(Colors.MediumPurple);
        var secondary = AccentSecondary as SolidColorBrush ?? new SolidColorBrush(Colors.DeepSkyBlue);
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, secondary.Color.R, secondary.Color.G, secondary.Color.B), 0.65));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.Color.R, accent.Color.G, accent.Color.B), 1));
        fill.Freeze();

        dc.DrawGeometry(fill, null, area);

        var lineBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        lineBrush.GradientStops.Add(new GradientStop(accent.Color, 0));
        lineBrush.GradientStops.Add(new GradientStop(secondary.Color, 1));
        lineBrush.Freeze();

        var pen = new Pen(lineBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Leading dot
        var last = points[^1];
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, secondary.Color.R, secondary.Color.G, secondary.Color.B)), null, last, 5, 5);
        dc.DrawEllipse(secondary, null, last, 2.6, 2.6);
    }

    private static Geometry BuildSmoothGeometry(Point[] points, bool closeToBottom, double height)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, closeToBottom ? height : points[0].Y), true, closeToBottom);
            for (var i = 0; i < points.Length - 1; i++)
            {
                var p0 = points[Math.Max(0, i - 1)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Math.Min(points.Length - 1, i + 2)];

                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.BezierTo(c1, c2, p2, true, false);
            }

            if (closeToBottom)
            {
                ctx.LineTo(new Point(points[^1].X, height), true, false);
                ctx.LineTo(new Point(points[0].X, height), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}

