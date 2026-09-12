using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipGlue.Controls;

/// <summary>
/// The small horizontal slider inside the preview's volume pill. Hand-drawn
/// for the same reason ScrollBarStyles has to parse XAML for the scrollbar:
/// a WPF <c>Slider</c> template has to hand its Track a Thumb and two
/// RepeatButtons through Track's own properties, which the imperative
/// FrameworkElementFactory builder used everywhere else in this project
/// cannot do (see gotcha #15 in CLAUDE.md). A trough, a fill and a knob are
/// three draw calls, so there is nothing to template around.
/// </summary>
public sealed class MiniSlider : DragTrack
{
    private const double TroughH = 4;
    private const double KnobR = 6;

    private double _value;

    public event Action<double>? ValueChanged;

    public MiniSlider(double width, double value)
    {
        Width = width;
        Height = KnobR * 2 + 4;
        _value = Math.Clamp(value, 0.0, 1.0);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Position on the track, 0..1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            double v = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(v - _value) < 0.0005) return;
            _value = v;
            InvalidateVisual();
        }
    }

    private double UsableW => Math.Max(1.0, ActualWidth - KnobR * 2);

    protected override void OnDragTo(double x)
    {
        double v = Math.Clamp((x - KnobR) / UsableW, 0.0, 1.0);
        if (Math.Abs(v - _value) < 0.0005) return;
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;

        // A transparent full-size rect so the whole strip is grabbable, not
        // just the 4px trough - FrameworkElement is only hit-testable where
        // it has actually drawn something.
        dc.DrawRectangle(Theme.TransparentBrush, null, new Rect(0, 0, w, h));

        double cy = h / 2.0;
        double x0 = KnobR, x1 = KnobR + UsableW;
        dc.DrawRoundedRectangle(Theme.DisabledBgBrush, null,
            new Rect(x0, cy - TroughH / 2, x1 - x0, TroughH), TroughH / 2, TroughH / 2);

        double kx = x0 + _value * UsableW;
        if (kx > x0)
            dc.DrawRoundedRectangle(Theme.FgBrush, null,
                new Rect(x0, cy - TroughH / 2, kx - x0, TroughH), TroughH / 2, TroughH / 2);

        dc.DrawEllipse(Theme.FgBrush, null, new Point(kx, cy), KnobR, KnobR);
    }
}
