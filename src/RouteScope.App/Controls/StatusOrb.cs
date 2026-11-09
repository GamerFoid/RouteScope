using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RouteScope.App.Controls;

/// <summary>
/// The big power orb on the home screen. A calm ring when idle, a breathing
/// accent ring with a travelling highlight when routing.
/// </summary>
public sealed class StatusOrb : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(StatusOrb),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(StatusOrb),
        new FrameworkPropertyMetadata(Color.FromRgb(0x7C, 0x6B, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Accent2Property = DependencyProperty.Register(
        nameof(Accent2), typeof(Color), typeof(StatusOrb),
        new FrameworkPropertyMetadata(Color.FromRgb(0x45, 0xC4, 0xF5), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackColorProperty = DependencyProperty.Register(
        nameof(TrackColor), typeof(Color), typeof(StatusOrb),
        new FrameworkPropertyMetadata(Color.FromRgb(0x26, 0x2B, 0x3D), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatusOrb),
        new FrameworkPropertyMetadata("\uE7E8", FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Color Accent2
    {
        get => (Color)GetValue(Accent2Property);
        set => SetValue(Accent2Property, value);
    }

    public Color TrackColor
    {
        get => (Color)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private double _phase;
    private bool _animating;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusOrb orb) orb.UpdateAnimation();
    }

    public StatusOrb()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    private void UpdateAnimation()
    {
        if (IsActive) StartAnimation();
        else StopAnimation();
        InvalidateVisual();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;

        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(5.5)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        BeginAnimation(PhaseProperty, anim);
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        BeginAnimation(PhaseProperty, null);
        _phase = 0;
        InvalidateVisual();
    }

    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(StatusOrb),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((StatusOrb)d)._phase = (double)e.NewValue));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var center = new Point(w / 2, h / 2);
        var radius = Math.Min(w, h) / 2 - 6;
        if (radius <= 4) return;

        // Ambient glow
        var glowRadius = radius + 14;
        var glow = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        var glowAlpha = IsActive ? (byte)0x50 : (byte)0x12;
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(glowAlpha, Accent.R, Accent.G, Accent.B), 0.62));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, Accent.R, Accent.G, Accent.B), 1));
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, glowRadius, glowRadius);

        // Track ring
        var trackPen = new Pen(new SolidColorBrush(TrackColor), 2);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        // Inner disc
        var innerRadius = radius - 9;
        var disc = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.35),
            GradientOrigin = new Point(0.5, 0.35),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        if (IsActive)
        {
            disc.GradientStops.Add(new GradientStop(Color.FromArgb(0x3A, Accent.R, Accent.G, Accent.B), 0));
            disc.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, Accent2.R, Accent2.G, Accent2.B), 1));
        }
        else
        {
            disc.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0));
            disc.GradientStops.Add(new GradientStop(Color.FromArgb(0x04, 0xFF, 0xFF, 0xFF), 1));
        }
        disc.Freeze();
        dc.DrawEllipse(disc, null, center, innerRadius, innerRadius);

        if (IsActive)
        {
            // Two rotating highlight arcs.
            DrawArc(dc, center, radius, _phase, 96, 3.2, Accent, 1.0);
            DrawArc(dc, center, radius, _phase + 180, 48, 2.2, Accent2, 0.7);

            // Soft outer pulse ring.
            var pulse = new Pen(new SolidColorBrush(Color.FromArgb(0x30, Accent.R, Accent.G, Accent.B)), 1);
            pulse.Freeze();
            dc.DrawEllipse(null, pulse, center, radius + 5, radius + 5);
        }

        // Glyph
        var glyphSize = radius * 0.62;
        var typeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, glyphSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var glyphColor = IsActive
            ? Color.FromRgb(0xF2, 0xF4, 0xFA)
            : Color.FromRgb(0x6A, 0x72, 0x88);

        var glyphBrush = new SolidColorBrush(glyphColor);
        var ft = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, glyphSize, glyphBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius,
        double startDegrees, double sweepDegrees, double thickness, Color color, double opacity)
    {
        var start = startDegrees * Math.PI / 180.0;
        var end = (startDegrees + sweepDegrees) * Math.PI / 180.0;

        var p1 = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var p2 = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(p2, new Size(radius, radius), 0,
            sweepDegrees > 180, SweepDirection.Clockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();

        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
