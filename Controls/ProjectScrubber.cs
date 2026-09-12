using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipGlue.Models;

namespace ClipGlue.Controls;

/// <summary>
/// The seek bar of the whole-project preview. Its time axis is the OUTPUT
/// timeline, not any one input file: every kept range is laid end to end,
/// exactly as the concat step will lay them, so the bar is a picture of the
/// finished clip before anything has been encoded.
///
/// Segments are drawn with a gap between them rather than as one continuous
/// fill, because seeing where the joins land is the main thing this preview
/// is for - a cut that lands badly is obvious from the bar as soon as you
/// know which stretch of playback belongs to which range.
/// </summary>
public sealed class ProjectScrubber : DragTrack
{
    public const double BarH = 26;

    private const double Radius = 7;
    private const double SegmentGap = 1.5;
    private const double KnobR = 6;

    private IReadOnlyList<TimeRange> _segments = Array.Empty<TimeRange>();
    private double _playhead;

    /// <summary>Raised with a position on the output timeline, in seconds.</summary>
    public event Action<double>? SeekRequested;

    public ProjectScrubber()
    {
        Height = BarH;
        Cursor = Cursors.Hand;
    }

    /// <summary>Segments are in OUTPUT time (each one starting where the
    /// previous ended), not in the source files' own time.</summary>
    public void SetModel(IReadOnlyList<TimeRange> segments, double total, double playhead)
    {
        _segments = segments;
        Total = Math.Max(0.001, total);
        _playhead = playhead;
        InvalidateVisual();
    }

    protected override bool CanStartDrag() => _segments.Count > 0;

    protected override void OnDragTo(double x) => SeekRequested?.Invoke(XToT(x));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2) return;

        double trackH = h - KnobR * 2 + 8;
        double trackY = (h - trackH) / 2.0;
        var shape = new RectangleGeometry(new Rect(0, trackY, w, trackH), Radius, Radius);
        dc.DrawGeometry(Theme.TimelineTrackBrush, null, shape);

        dc.PushClip(shape);
        double px = TToX(_playhead);
        foreach (var seg in _segments)
        {
            double xa = TToX(seg.Start) + SegmentGap;
            double xb = TToX(seg.End) - SegmentGap;
            if (xb <= xa) xb = xa + 1;

            dc.DrawRectangle(Theme.RangeFillBrush, null, new Rect(xa, trackY, xb - xa, trackH));
            // The played part of this segment, so progress reads against the
            // structure rather than as one anonymous bar.
            if (px > xa)
                dc.DrawRectangle(Theme.FgAccentBrush, null,
                    new Rect(xa, trackY, Math.Min(px, xb) - xa, trackH));
        }
        dc.Pop();

        dc.DrawRoundedRectangle(null, new Pen(Theme.BorderCardBrush, 1),
            new Rect(0.5, trackY + 0.5, w - 1, trackH - 1), Radius, Radius);

        if (_segments.Count == 0) return;
        dc.DrawEllipse(Theme.FgBrush, new Pen(Theme.VideoBgBrush, 1.5), new Point(px, h / 2.0), KnobR, KnobR);
    }
}
