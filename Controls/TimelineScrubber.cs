using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipGlue.Models;
using ClipGlue.Views;

namespace ClipGlue.Controls;

/// <summary>Which grip the pointer is on, and therefore what a drag moves.</summary>
public enum ScrubberGrip { None, In, Out, Playhead }

/// <summary>
/// The zoomed timeline. Unlike <see cref="TimelineOverview"/> it shows only
/// the current viewport, so at anything past Fit one pixel is a fraction of a
/// second and a cut can actually be placed by dragging.
///
/// Four layers, bottom to top: a filmstrip of decoded frames sampled across
/// the viewport, a scrim that keeps everything above readable over a bright
/// frame, the range bands (translucent here, unlike the solid ones on the
/// overview bar, so the picture still shows through), then the markers -
/// keyframe ticks along the bottom, a white pennant playhead, and square
/// accent grips for in/out. Square grips against a round-topped playhead is
/// the whole point: before this they were identical circles and there was no
/// way to tell at a glance which marker was which.
///
/// The control owns the drag and snap arithmetic but no state of record - it
/// reports what the user did and the window decides.
/// </summary>
public sealed class TimelineScrubber : FrameworkElement
{
    public const double BubbleH = 22;
    public const double BubbleGap = 6;
    public const double TrackH = 58;
    public const double TotalH = BubbleH + BubbleGap + TrackH;

    private const double TrackY = BubbleH + BubbleGap;
    private const double Radius = 8;
    private const double GripW = 11;
    private const double GripHitPx = 9;
    private const double TickBand = 9;
    private const double MinTickGap = 7;
    private const int MaxCells = 16;

    private readonly ThumbnailStrip _thumbs;
    private readonly double _videoAspect;

    private double _duration = 1;
    private double _viewStart, _viewEnd = 1;
    private IReadOnlyList<TimeRange> _ranges = Array.Empty<TimeRange>();
    private int _editIndex = -1;
    private double _inT, _outT, _playhead;
    private IReadOnlyList<double> _keyframes = Array.Empty<double>();
    private ScrubberGrip _grip = ScrubberGrip.None;
    private ScrubberGrip _selected = ScrubberGrip.None;
    private double _hoverT = double.NaN;

    private double[] _cellTimes = Array.Empty<double>();
    private double _cellW;

    public bool SnapEnabled { get; set; } = true;

    /// <summary>Fires while the playhead is dragged, or when the track is clicked.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Fires with the new (in, out) while a grip is dragged. Both are
    /// reported because a grip dragged past its partner swaps roles.</summary>
    public event Action<double, double>? RangeEdited;

    /// <summary>Mouse wheel: +1 in, -1 out, anchored on the time under the pointer.</summary>
    public event Action<int, double>? ZoomStepRequested;

    /// <summary>Raised when a press lands, so playback can stop before the
    /// play timer starts fighting the drag for the playhead.</summary>
    public event Action? Grabbed;

    public TimelineScrubber(ThumbnailStrip thumbs, int videoW, int videoH)
    {
        _thumbs = thumbs;
        _videoAspect = videoH > 0 ? Math.Clamp((double)videoW / videoH, 0.4, 4.0) : 16.0 / 9.0;
        Height = TotalH;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
    }

    /// <summary>The grip the user last clicked, which is what the arrow keys
    /// nudge. None once the playhead has been moved instead.</summary>
    public ScrubberGrip Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    public void SetModel(double duration, double viewStart, double viewEnd,
        IReadOnlyList<TimeRange> ranges, int editIndex, double inT, double outT, double playhead)
    {
        bool viewMoved = Math.Abs(viewStart - _viewStart) > 1e-6 || Math.Abs(viewEnd - _viewEnd) > 1e-6;
        _duration = Math.Max(0.001, duration);
        _viewStart = viewStart;
        _viewEnd = Math.Max(viewStart + 0.001, viewEnd);
        _ranges = ranges;
        _editIndex = editIndex;
        _inT = inT;
        _outT = outT;
        _playhead = playhead;
        if (viewMoved) RebuildFilmstrip();
        InvalidateVisual();
    }

    public void SetKeyframes(IReadOnlyList<double> keyframes)
    {
        _keyframes = keyframes;
        InvalidateVisual();
    }

    // -- geometry ---------------------------------------------------------------

    private double ViewSpan => _viewEnd - _viewStart;
    private double SecondsPerPixel => ViewSpan / Math.Max(1.0, ActualWidth);

    private double TToX(double t) => (t - _viewStart) / ViewSpan * ActualWidth;

    private double XToT(double x) =>
        Math.Clamp(_viewStart + x / Math.Max(1.0, ActualWidth) * ViewSpan, 0, _duration);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (info.WidthChanged) RebuildFilmstrip();
    }

    /// <summary>Recomputes which timestamps the filmstrip needs and asks for
    /// them. Kept out of OnRender deliberately: render runs on every playhead
    /// tick, and queueing ffmpeg work from a paint pass would relaunch the
    /// whole strip 25 times a second.</summary>
    public void RebuildFilmstrip()
    {
        double w = ActualWidth;
        if (w < 40 || _duration <= 0)
        {
            _cellTimes = Array.Empty<double>();
            return;
        }

        double idealW = Math.Max(48.0, TrackH * _videoAspect);
        int count = Math.Clamp((int)Math.Ceiling(w / idealW), 1, MaxCells);
        _cellW = w / count;

        var times = new double[count];
        for (int i = 0; i < count; i++)
            times[i] = Math.Clamp(_viewStart + (i + 0.5) / count * ViewSpan, 0, Math.Max(0, _duration - 0.05));
        _cellTimes = times;

        _thumbs.DecodeWidth = Math.Clamp((int)Math.Round(_cellW * 1.5 / 2) * 2, 64, 240);
        _thumbs.Request(times);
    }

    // -- interaction ------------------------------------------------------------

    private ScrubberGrip HitTest(double x)
    {
        double dIn = Math.Abs(x - TToX(_inT));
        double dOut = Math.Abs(x - TToX(_outT));
        // Out wins a tie: a fresh range starts zero-length with both grips
        // stacked, and dragging away from that point is always dragging the
        // end forward.
        if (dOut <= GripHitPx && dOut <= dIn) return ScrubberGrip.Out;
        if (dIn <= GripHitPx) return ScrubberGrip.In;
        return ScrubberGrip.Playhead;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        CaptureMouse();
        Grabbed?.Invoke();

        double x = e.GetPosition(this).X;
        _grip = HitTest(x);
        if (_grip == ScrubberGrip.Playhead)
        {
            _selected = ScrubberGrip.None;
            SeekRequested?.Invoke(XToT(x));
        }
        else
        {
            _selected = _grip;
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;

        if (_grip == ScrubberGrip.None)
        {
            double t = XToT(x);
            if (double.IsNaN(_hoverT) || Math.Abs(t - _hoverT) > SecondsPerPixel * 0.5)
            {
                _hoverT = t;
                InvalidateVisual();
            }
            return;
        }

        if (_grip == ScrubberGrip.Playhead)
        {
            SeekRequested?.Invoke(XToT(x));
            return;
        }

        double raw = Snap(XToT(x));
        double newIn = _inT, newOut = _outT;
        if (_grip == ScrubberGrip.In)
        {
            // Dragged past its partner a grip swaps identity rather than
            // sticking, which is what makes a zero-length new range - both
            // grips on the same spot - draggable in either direction.
            if (raw > _outT) { newIn = _outT; newOut = raw; _grip = ScrubberGrip.Out; }
            else newIn = raw;
        }
        else
        {
            if (raw < _inT) { newOut = _inT; newIn = raw; _grip = ScrubberGrip.In; }
            else newOut = raw;
        }
        _selected = _grip;
        _hoverT = raw;
        RangeEdited?.Invoke(newIn, newOut);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _grip = ScrubberGrip.None;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_grip != ScrubberGrip.None) return;
        _hoverT = double.NaN;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomStepRequested?.Invoke(Math.Sign(e.Delta), XToT(e.GetPosition(this).X));
    }

    /// <summary>Pulls a time onto the nearest keyframe when it is close
    /// enough. The tolerance is pixel-proportional with a 0.5s floor: at Fit
    /// half a second can be sub-pixel and snapping would look like nothing at
    /// all, while zoomed right in a fixed pixel window would be looser than
    /// the precision the user just zoomed in to get.</summary>
    public double Snap(double t)
    {
        if (!SnapEnabled || _keyframes.Count == 0) return t;
        double tol = Math.Max(0.5, SecondsPerPixel * 6);
        double best = t;
        double bestD = double.MaxValue;
        foreach (double k in _keyframes)
        {
            double d = Math.Abs(k - t);
            if (d < bestD) { bestD = d; best = k; }
            else if (k > t) break;   // sorted, so nothing later can be closer
        }
        return bestD <= tol ? best : t;
    }

    /// <summary>True when a value sits exactly on a keyframe - drives the
    /// magnet badge next to the Start/End fields.</summary>
    public bool IsOnKeyframe(double t)
    {
        foreach (double k in _keyframes)
        {
            if (Math.Abs(k - t) < 0.005) return true;
            if (k > t + 0.005) break;
        }
        return false;
    }

    // -- drawing ------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 4) return;

        var trackRect = new Rect(0, TrackY, w, TrackH);
        var shape = new RectangleGeometry(trackRect, Radius, Radius);
        dc.DrawGeometry(Theme.TimelineTrackBrush, null, shape);

        dc.PushClip(shape);
        DrawFilmstrip(dc);
        dc.DrawRectangle(Theme.FilmScrimBrush, null, trackRect);
        DrawRangeBands(dc);
        DrawKeyframeTicks(dc);
        DrawPlayhead(dc);
        DrawGrip(dc, _inT, _selected == ScrubberGrip.In);
        DrawGrip(dc, _outT, _selected == ScrubberGrip.Out);
        dc.Pop();

        dc.DrawRoundedRectangle(null, new Pen(Theme.BorderCardBrush, 1),
            new Rect(0.5, TrackY + 0.5, w - 1, TrackH - 1), Radius, Radius);

        DrawBubble(dc, w);
    }

    private void DrawFilmstrip(DrawingContext dc)
    {
        if (_cellTimes.Length == 0) return;
        for (int i = 0; i < _cellTimes.Length; i++)
        {
            var bmp = _thumbs.Get(_cellTimes[i]);
            if (bmp is null) continue;
            // +1 on the width closes the hairline seam that rounding to
            // device pixels otherwise leaves between neighbouring cells.
            dc.DrawImage(bmp, new Rect(i * _cellW, TrackY, _cellW + 1, TrackH));
        }
    }

    private void DrawRangeBands(DrawingContext dc)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            if (i == _editIndex) continue;         // the live one is drawn last, on top
            if (r.End <= _viewStart || r.Start >= _viewEnd || r.Length <= 0) continue;
            DrawBand(dc, r.Start, r.End, Theme.RangeFillSoftBrush, Theme.FgLinkBrush);
        }
        if (_outT > _inT)
            DrawBand(dc, _inT, _outT, Theme.RangeAccentSoftBrush, Theme.FgAccentBrush, glow: true);
    }

    private void DrawBand(DrawingContext dc, double start, double end, Brush fill, Brush edge, bool glow = false)
    {
        double xa = TToX(start), xb = TToX(end);
        double w = Math.Max(1.5, xb - xa);
        if (glow) DrawBandGlow(dc, xa, xb);
        dc.DrawRectangle(fill, null, new Rect(xa, TrackY, w, TrackH));
        // A solid cap along the top as well as the wash. The wash alone has
        // to stay light enough to see the frames through, which over a busy
        // filmstrip leaves the band hard to spot at a glance; the cap is the
        // part that actually reads as "this stretch is selected".
        dc.DrawRectangle(edge, null, new Rect(xa, TrackY, w, 3));
        dc.DrawRectangle(edge, null, new Rect(xa - 0.75, TrackY, 1.5, TrackH));
        dc.DrawRectangle(edge, null, new Rect(xb - 0.75, TrackY, 1.5, TrackH));
        if (glow) dc.DrawRectangle(CapBloom, null, new Rect(xa, TrackY, w, CapBloomH));
    }

    // -- selection glow ---------------------------------------------------------
    // Hand-drawn, because this control paints itself in OnRender and has no
    // element to hang a WPF Effect on - and a BitmapEffect/DropShadowEffect
    // would be the wrong tool anyway: it forces an offscreen render surface
    // for a control that repaints on every scrub tick and on every filmstrip
    // thumbnail that arrives.
    //
    // The glow is therefore ONE horizontal gradient on each side of the fill,
    // plus one vertical gradient bleeding down from the top cap: three extra
    // draw calls, all with frozen brushes, versus an intermediate surface per
    // frame.
    //
    // The first attempt used three flat bands of falling alpha per side. At
    // 3x magnification those read as visible steps rather than a falloff, and
    // they cost six draw calls instead of two - so the gradient is both the
    // better picture and the cheaper one.

    private const double GlowW = 12;
    private const double CapBloomH = 16;

    /// <summary>Frozen and static: the selection is always the accent color,
    /// so there is nothing per-instance to build and nothing to allocate per
    /// frame. Two brushes because the falloff has to run outward from the
    /// fill on each side, which means the right one is mirrored.</summary>
    private static readonly Brush GlowLeft = BuildSideGlow(towardRight: true);
    private static readonly Brush GlowRight = BuildSideGlow(towardRight: false);
    private static readonly Brush CapBloom = BuildCapBloom();

    private static Brush BuildSideGlow(bool towardRight)
    {
        var c = Theme.AccentGold500;
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        // 0x38 hard against the fill, fading to nothing at the outer edge -
        // subtle enough that the filmstrip still reads through it.
        var near = Color.FromArgb(0x38, c.R, c.G, c.B);
        var far = Color.FromArgb(0x00, c.R, c.G, c.B);
        b.GradientStops.Add(new GradientStop(towardRight ? far : near, 0));
        b.GradientStops.Add(new GradientStop(towardRight ? near : far, 1));
        b.Freeze();
        return b;
    }

    private static Brush BuildCapBloom()
    {
        var c = Theme.AccentGold500;
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1));
        b.Freeze();
        return b;
    }

    private void DrawBandGlow(DrawingContext dc, double xa, double xb)
    {
        dc.DrawRectangle(GlowLeft, null, new Rect(xa - GlowW, TrackY, GlowW, TrackH));
        dc.DrawRectangle(GlowRight, null, new Rect(xb, TrackY, GlowW, TrackH));
    }

    private void DrawKeyframeTicks(DrawingContext dc)
    {
        if (_keyframes.Count == 0) return;

        int visible = 0;
        foreach (double k in _keyframes)
        {
            if (k < _viewStart) continue;
            if (k > _viewEnd) break;
            visible++;
        }
        // Zoomed far enough out, the ticks are closer together than they are
        // wide and collapse into a dashed band that says nothing about where
        // any individual keyframe is. Drawing none at that point is honest;
        // they reappear as soon as the zoom makes them separable.
        if (visible == 0 || ActualWidth / visible < MinTickGap) return;

        double y = TrackY + TrackH - TickBand;
        double lastX = double.NegativeInfinity;
        foreach (double k in _keyframes)
        {
            if (k < _viewStart) continue;
            if (k > _viewEnd) break;
            double x = TToX(k);
            if (x - lastX < MinTickGap) continue;
            lastX = x;
            dc.DrawRectangle(Theme.KeyframeTickBrush, null, new Rect(x - 0.5, y, 1, TickBand));
        }
    }

    private void DrawPlayhead(DrawingContext dc)
    {
        if (_playhead < _viewStart || _playhead > _viewEnd) return;
        double x = TToX(_playhead);
        dc.DrawRectangle(Theme.FgBrush, null, new Rect(x - 1, TrackY, 2, TrackH));

        // The pennant is what separates the playhead from the grips at a
        // glance, so it hangs off the top of the line rather than sitting
        // symmetrically on it the way the square grips do.
        var flag = new StreamGeometry();
        using (var g = flag.Open())
        {
            g.BeginFigure(new Point(x - 5, TrackY), true, true);
            g.LineTo(new Point(x + 5, TrackY), true, false);
            g.LineTo(new Point(x + 5, TrackY + 6), true, false);
            g.LineTo(new Point(x, TrackY + 10), true, false);
            g.LineTo(new Point(x - 5, TrackY + 6), true, false);
        }
        flag.Freeze();
        dc.DrawGeometry(Theme.FgBrush, null, flag);
    }

    private void DrawGrip(DrawingContext dc, double t, bool selected)
    {
        if (t < _viewStart - 1 || t > _viewEnd + 1) return;
        double x = TToX(t);
        double cy = TrackY + TrackH / 2.0;
        var box = new Rect(x - GripW / 2, cy - GripW / 2, GripW, GripW);
        dc.DrawRoundedRectangle(Theme.FgAccentBrush,
            new Pen(selected ? Theme.FgBrush : Theme.AccentDarkBrush, selected ? 2 : 1), box, 2, 2);
    }

    private void DrawBubble(DrawingContext dc, double w)
    {
        if (double.IsNaN(_hoverT)) return;
        bool dragging = _grip is ScrubberGrip.In or ScrubberGrip.Out;

        var text = new FormattedText(TimeUtils.FormatTime(_hoverT), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Theme.MonoFontFamily, FontStyles.Normal,
                dragging ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            Theme.MonoFontSize, dragging ? Theme.FgAccentBrush : Theme.FgDimBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double bw = text.Width + 18;
        double bx = Math.Clamp(TToX(_hoverT) - bw / 2, 0, Math.Max(0, w - bw));
        dc.DrawRoundedRectangle(Theme.OverlayPillBrush, new Pen(Theme.BorderCardBrush, 1),
            new Rect(bx, 0, bw, BubbleH), Radius, Radius);
        dc.DrawText(text, new Point(bx + 9, (BubbleH - text.Height) / 2));
    }
}
