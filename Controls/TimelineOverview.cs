using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipGlue.Models;

namespace ClipGlue.Controls;

/// <summary>
/// The "full clip overview" minimap: one thin bar that always shows the WHOLE
/// file, with every range marked on it and a translucent window showing which
/// slice of the file the zoomed scrubber below is currently displaying.
///
/// This exists because the old single flat bar was the only view of the
/// timeline: on a 23-minute file across ~940px that is about 1.5 seconds per
/// pixel, which is not enough resolution to land a cut by dragging, and
/// ranges already added were invisible on it. Splitting navigation (here)
/// from precision (the scrubber) fixes both at once.
///
/// Ranges are drawn SOLID here, unlike on the scrubber, because there are no
/// thumbnails behind them to keep readable.
/// </summary>
public sealed class TimelineOverview : DragTrack
{
    public const double BarH = 22;

    private const double Radius = 6;

    private IReadOnlyList<TimeRange> _ranges = Array.Empty<TimeRange>();
    private int _editIndex = -1;
    private double _playhead;
    private double _viewStart, _viewEnd;

    /// <summary>Raised with the time the user wants the zoomed viewport
    /// centered on. The window owns the viewport, so this only asks.</summary>
    public event Action<double>? ViewCenterRequested;

    public TimelineOverview()
    {
        Height = BarH;
        Cursor = Cursors.Hand;
        ToolTip = "Whole file - click or drag to move the zoomed track below";
    }

    public void SetModel(double duration, IReadOnlyList<TimeRange> ranges, int editIndex,
        double playhead, double viewStart, double viewEnd)
    {
        Total = Math.Max(0.001, duration);
        _ranges = ranges;
        _editIndex = editIndex;
        _playhead = playhead;
        _viewStart = viewStart;
        _viewEnd = viewEnd;
        InvalidateVisual();
    }

    protected override void OnDragTo(double x) => ViewCenterRequested?.Invoke(XToT(x));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2) return;

        var shape = new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius);
        dc.DrawGeometry(Theme.TimelineTrackBrush, null, shape);

        dc.PushClip(shape);
        for (int i = 0; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            if (r.Length <= 0) continue;
            bool edit = i == _editIndex;
            double xa = TToX(r.Start), xb = TToX(r.End);
            dc.DrawRectangle(edit ? Theme.RangeAccentFillBrush : Theme.RangeFillBrush, null,
                new Rect(xa, 0, Math.Max(1.5, xb - xa), h));
        }

        // The viewport window, only when it is actually a window onto
        // something - at Fit it covers the whole bar and would just add a
        // pointless outline around the entire control.
        double span = _viewEnd - _viewStart;
        if (span > 0 && span < Total - 0.001)
        {
            double vx0 = TToX(_viewStart), vx1 = TToX(_viewEnd);
            var box = new Rect(vx0, 0.5, Math.Max(3, vx1 - vx0), h - 1);
            dc.DrawRoundedRectangle(Theme.ViewportWashBrush, new Pen(Theme.ViewportEdgeBrush, 1), box, 3, 3);
        }

        double px = TToX(_playhead);
        dc.DrawRectangle(Theme.FgBrush, null, new Rect(px - 1, 0, 2, h));
        dc.Pop();

        dc.DrawRoundedRectangle(null, new Pen(Theme.BorderCardBrush, 1),
            new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
    }
}
