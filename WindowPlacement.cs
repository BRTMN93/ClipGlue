using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClipGlue;

/// <summary>
/// Pins every window this app opens flush with the TOP of the work area of
/// the screen it lands on.
///
/// The reason is the BOTTOM edge, not the top one. Every window here puts
/// its action buttons on its last row - START/STOP and the progress bars in
/// the main window, Apply / Add range / Cancel in Trim range, the buttons of
/// the confirm dialogs - and all of them are auto-height-sized to a
/// natural height that on this machine sits within a few tens of DIP of the
/// whole work area (main window ~797 of 912, Trim range ~865 of 912).
/// Anything that pushes them down therefore runs that last row off the
/// bottom of the desktop or under the taskbar, where it can be neither
/// clicked nor seen - and because those windows are already sitting at their
/// own MinHeight, it cannot be dragged back into view either. Two things
/// pushed them down: Windows' own cascade placement for the ownerless main
/// window, and WindowStartupLocation.CenterOwner for the owned ones, which
/// spends half of any leftover height above the window where nothing needs
/// it. Starting at the top spends the entire screen height downwards, which
/// is the only direction that keeps the buttons reachable.
///
/// This is placement only. Nothing here changes how tall a window wants to
/// be; the one height it does write is the last-resort clamp for a window
/// that is taller than the screen it opened on, which has no good resting
/// place at any position.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Moves the window's top edge to the top of its monitor's work area and
    /// pulls it horizontally back onto that monitor if it hangs off the
    /// side. Call it once the window's final size is known - i.e. from
    /// <c>Loaded</c>, after any SizeToContent measurement has settled -
    /// because a window is positioned before that measurement, not after,
    /// and its own left/top would otherwise be computed for the wrong size.
    /// </summary>
    public static void PinToTop(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;

        Rect work = WorkAreaFor(window);
        if (work.Width <= 0 || work.Height <= 0) return;

        // Take placement away from WPF before writing anything, or the write
        // does not stick. A window whose height is still owned by
        // SizeToContent gets RE-POSITIONED by its WindowStartupLocation
        // every time that measurement changes the size - and for a dialog
        // that happens after Loaded, so CenterOwner quietly undoes the Top
        // set below. Measured: the two SizeToContent dialogs came back
        // centred on their owner while the two windows that had already
        // switched to SizeToContent.Manual pinned correctly. Manual also
        // means a dialog that grows later grows DOWNWARDS from the top of
        // the screen, into the room this pinning exists to give it, instead
        // of re-centring and walking its buttons back towards the bottom
        // edge.
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        // Height next: a top edge is only worth setting if the window
        // actually fits below it. MinHeight has to come down with it, since
        // that is precisely what makes a window unshrinkable - every window
        // here already caps its own MinHeight against the work area for the
        // same reason, so this only catches the case where that cap was
        // computed for a different (taller) monitor.
        if (!IsHeightAutoSized(window))
        {
            double h = EffectiveHeight(window);
            if (h > work.Height)
            {
                if (window.MinHeight > work.Height) window.MinHeight = work.Height;
                window.Height = work.Height;
            }
        }

        window.Top = work.Top;

        // Only the top edge moves; horizontally an owned window stays
        // centred on its owner and the ownerless main window stays where
        // CenterScreen put it. The centring is recomputed here rather than
        // read back off window.Left, because WPF centres a SizeToContent
        // window against the size it had BEFORE its content was measured -
        // the notice dialogs came back 480 DIP left of their owner's centre
        // that way - and what normally hides that is the very re-position
        // pass disabled above. Recomputing uses the final size, so it lands
        // right the first time instead of being corrected afterwards.
        double w = EffectiveWidth(window);
        if (w <= 0) return;

        double left = window.Left;
        if (TryGetScreenRect(window.Owner, out Rect owner))
            left = owner.Left + (owner.Width - w) / 2.0;
        if (double.IsNaN(left)) return;

        window.Left = w <= work.Width ? Math.Clamp(left, work.Left, work.Right - w) : work.Left;
    }

    /// <summary>
    /// Centers a window on its owner - for small notice dialogs (the "Done"/
    /// error notices) that have no last-row-of-buttons-off-the-bottom risk
    /// PinToTop exists to prevent, so pinning them to the top of the screen
    /// just reads as oddly placed instead of as "a message about this
    /// window". Requested by Bartek after PinToTop made the Done/error
    /// notices land near the top edge of the main window instead of centred
    /// over it.
    ///
    /// Still has to do its own math rather than trust
    /// WindowStartupLocation.CenterOwner, for the same two reasons PinToTop's
    /// horizontal centring already works around: WPF centres a
    /// SizeToContent window against the size it had BEFORE its content was
    /// measured (see PinToTop's comment - the earlier bug this same class of
    /// dialog hit), and a maximized owner's Window.Left/ActualWidth report
    /// its RESTORED position, not where it currently is, so TryGetScreenRect
    /// (a real GetWindowRect) is used instead.
    /// </summary>
    public static void CenterOnOwner(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;

        // Same reason as PinToTop: take placement away from WPF before
        // writing anything, or a later SizeToContent remeasure re-centres
        // the window against its own (by-then stale) idea of the size and
        // quietly undoes this.
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        double w = EffectiveWidth(window);
        double h = EffectiveHeight(window);
        if (w <= 0 || h <= 0) return;

        double left = window.Left, top = window.Top;
        if (TryGetScreenRect(window.Owner, out Rect owner))
        {
            left = owner.Left + (owner.Width - w) / 2.0;
            top = owner.Top + (owner.Height - h) / 2.0;
        }
        if (double.IsNaN(left) || double.IsNaN(top)) return;

        Rect work = WorkAreaFor(window);
        if (work.Width > 0) left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
        if (work.Height > 0) top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));

        window.Left = left;
        window.Top = top;
    }

    /// <summary>
    /// Pulls a window back up if a height change has just pushed its bottom
    /// edge - and with it the action row - below the work area. Only ever
    /// moves it upwards, and never above the top of the screen, so a window
    /// the user has positioned themselves stays exactly where they put it
    /// for as long as it still fits there.
    /// </summary>
    public static void KeepBottomOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;

        Rect work = WorkAreaFor(window);
        if (work.Height <= 0) return;

        double h = EffectiveHeight(window);
        double top = window.Top;
        if (h <= 0 || double.IsNaN(top)) return;

        double maxTop = work.Bottom - h;
        if (top > maxTop) window.Top = Math.Max(work.Top, maxTop);
    }

    /// <summary>The work area (the screen minus the taskbar) of the monitor
    /// the window currently sits on, in DIPs - the same unit as
    /// Window.Left/Top/Width/Height.</summary>
    public static Rect WorkAreaFor(Window window)
    {
        // SystemParameters.WorkArea is the primary monitor's, full stop, so
        // it is only the fallback here: CLAUDE.md already records that this
        // desktop can have monitors at negative coordinates, and a window
        // opened on one of those must be pinned to ITS top, not the primary
        // screen's.
        if (PresentationSource.FromVisual(window) is not HwndSource source
            || source.CompositionTarget is null || source.Handle == IntPtr.Zero)
            return SystemParameters.WorkArea;

        IntPtr monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return SystemParameters.WorkArea;

        // Window.Left/Top are DIPs scaled by THIS window's DPI (WPF on .NET
        // Core is Per-Monitor-V2 aware, see CLAUDE.md "improvements" #1), so
        // the monitor rectangle - which the API hands back in physical
        // pixels - has to be divided by that same window's scale rather than
        // by the primary monitor's.
        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        Point topLeft = fromDevice.Transform(new Point(info.RcWork.Left, info.RcWork.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.RcWork.Right, info.RcWork.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>The window's rectangle on screen in DIPs, straight from the
    /// OS. Window.Left/Width cannot stand in for this: a maximized window
    /// reports its RESTORED position through those properties, so centring a
    /// dialog on a maximized owner off them lands it wherever that owner
    /// last was before it was maximized.</summary>
    private static bool TryGetScreenRect(Window window, out Rect rect)
    {
        rect = default;
        if (window is null) return false;
        if (PresentationSource.FromVisual(window) is not HwndSource source
            || source.CompositionTarget is null || source.Handle == IntPtr.Zero)
            return false;
        if (!GetWindowRect(source.Handle, out NativeRect r)) return false;

        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        rect = new Rect(fromDevice.Transform(new Point(r.Left, r.Top)),
                        fromDevice.Transform(new Point(r.Right, r.Bottom)));
        return true;
    }

    /// <summary>True while SizeToContent still owns the height, in which
    /// case writing Height does nothing and the window's own content
    /// constraints (an inner ScrollViewer's MaxHeight, typically) are what
    /// bound it.</summary>
    private static bool IsHeightAutoSized(Window window)
        => window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

    // Height/Width win over ActualHeight/ActualWidth when they are set,
    // because these are called right after a height has been written and
    // before the layout pass that would make Actual* agree with it.
    private static double EffectiveHeight(Window window)
        => !double.IsNaN(window.Height) && window.Height > 0 ? window.Height : window.ActualHeight;

    private static double EffectiveWidth(Window window)
        => !double.IsNaN(window.Width) && window.Width > 0 ? window.Width : window.ActualWidth;

    // -- Win32 ----------------------------------------------------------------

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public int DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
