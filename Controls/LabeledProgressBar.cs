using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClipGlue.Controls;

/// <summary>
/// A slim, rounded progress bar drawn on a FrameworkElement's DrawingContext,
/// with a centered "NN%" label - direct port of widgets.py's
/// LabeledProgressBar (there drawn on a tk.Canvas, since ttk.Progressbar has
/// no way to show a label at all).
///
/// The label always sits at the horizontal center, so its color only ever
/// needs to flip once: while the fill hasn't reached the middle yet the
/// label sits on the dark trough and stays light/readable there; once the
/// fill passes the middle the label sits on the accent-colored fill
/// instead, so it swaps to <see cref="TextDark"/> right at that point
/// (<see cref="Adaptive"/>).
/// </summary>
public sealed class LabeledProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LabeledProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var v = (double)baseValue;
        return Math.Max(0.0, Math.Min(100.0, v));
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush FillColor { get; set; } = ClipGlue.Theme.FgAccentBrush;
    public Brush TroughColor { get; set; } = ClipGlue.Theme.BgInputBrush;
    public Brush BorderColor { get; set; } = ClipGlue.Theme.BorderCardBrush;
    public Brush TextLight { get; set; } = ClipGlue.Theme.FgBrush;
    public Brush TextDark { get; set; } = ClipGlue.Theme.BgBrush;
    public bool Adaptive { get; set; } = true;
    /// <summary>Whether the centred "NN%" is drawn inside the bar. The
    /// redesign puts the percentage on a readout line under the bar instead,
    /// so the bar itself stays a clean shape; the label is kept for any bar
    /// that still has nowhere else to put it.</summary>
    public bool ShowLabel { get; set; } = true;

    public LabeledProgressBar()
    {
        Height = ClipGlue.Theme.BarH;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Never let the radius exceed a half-height, or the two corner arcs
        // on one end would overlap and pinch the bar's outline.
        double radius = Math.Min(ClipGlue.Theme.BarRadius, h / 2.0);
        var trough = new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));

        var pen = new Pen(BorderColor, 1);
        pen.Freeze();
        dc.DrawRoundedRectangle(TroughColor, pen, trough, radius, radius);

        double fillW = w * (Value / 100.0);
        if (fillW > 0.5)
        {
            // The fill is a plain rectangle clipped to the trough's rounded
            // silhouette: that rounds its left end to match the trough while
            // leaving its leading edge a straight vertical line, so the bar
            // still reads as an exact position rather than a soft blob.
            dc.PushClip(new RectangleGeometry(trough, radius, radius));
            dc.DrawRectangle(FillColor, null, new Rect(0, 0, fillW, h));
            dc.Pop();
        }

        if (!ShowLabel) return;

        // Truncated, not rounded: rounding could show e.g. "62%" while the
        // fill itself is only 61.6% across - int() always reports "at least
        // this much is done", matching what the fill width shows.
        var text = $"{(int)Value}%";
        var typeface = new Typeface(ClipGlue.Theme.UiFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var brush = (Adaptive && Value >= 50) ? TextDark : TextLight;
        var ft = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
            ClipGlue.Theme.BarFontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((w - ft.Width) / 2.0, (h - ft.Height) / 2.0));
    }
}
