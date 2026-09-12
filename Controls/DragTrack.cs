using System.Windows;
using System.Windows.Input;

namespace ClipGlue.Controls;

/// <summary>
/// Shared click-and-drag plumbing for a horizontal scrub/slider track:
/// capture the mouse on left-button-down, report the pixel-space X while
/// dragging, release on button-up. Pulled out of MiniSlider, ProjectScrubber
/// and TimelineOverview, which all hand-rolled this exact sequence.
/// </summary>
public abstract class DragTrack : FrameworkElement
{
    private bool _dragging;

    /// <summary>The axis extent TToX/XToT map against - a clip's duration,
    /// or an output timeline's total length. Subclasses set it from their
    /// own SetModel.</summary>
    protected double Total { get; set; } = 1;

    protected double TToX(double t) => Math.Clamp(t / Total, 0, 1) * ActualWidth;
    protected double XToT(double x) => Math.Clamp(x / Math.Max(1.0, ActualWidth), 0, 1) * Total;

    /// <summary>Called with the mouse's X (in this element's own
    /// coordinates) on mouse-down and on every mouse-move while dragging.</summary>
    protected abstract void OnDragTo(double x);

    /// <summary>Override to veto starting a drag (e.g. nothing to scrub
    /// yet). Defaults to always allowed.</summary>
    protected virtual bool CanStartDrag() => true;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!CanStartDrag()) return;
        _dragging = true;
        CaptureMouse();
        OnDragTo(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) OnDragTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragging = false;
        ReleaseMouseCapture();
    }
}
