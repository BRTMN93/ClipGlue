using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipGlue;

/// <summary>
/// The OS-level half of the custom title bar (see Controls/TitleBar.cs):
/// native rounded window corners, a dark-themed non-client frame, and a
/// correct maximized size - none of which WindowChrome gives for free.
///
/// Rounded corners/dark frame are DwmSetWindowAttribute calls. Measured
/// available on this machine (build 26200) before this was written - see
/// PROJECT_STATE.md's redesign phase 0 decision 4 - so applied
/// unconditionally; on an older Windows build that doesn't recognise these
/// attribute ids, DwmSetWindowAttribute just returns a failure HRESULT
/// (ignored here) instead of throwing, leaving the window with square
/// corners and the default light-frame edge.
///
/// The maximized size is a WM_GETMINMAXINFO hook, and it turned out to be
/// needed after all despite the phase 0 note's optimism that WindowChrome
/// would handle it alone: a WindowStyle=None window maximizes to the full
/// MONITOR rectangle (screen size), not the WORK AREA (screen minus
/// taskbar), because Windows only shrinks a maximized window to the work
/// area automatically for windows that still have a native, styled
/// non-client frame - which this one no longer does. Observed: without this
/// hook, maximizing covered the taskbar. WindowPlacement.cs already solves
/// the "which work area" question the same way (MonitorFromWindow +
/// GetMonitorInfo on THIS window's monitor, not SystemParameters.WorkArea,
/// which only ever describes the primary one) for window placement; this is
/// the same fix applied to WM_GETMINMAXINFO's own maximize geometry
/// instead, since that message hands raw Win32 structures a WPF Rect can't
/// stand in for.
/// </summary>
public static class NativeChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 PtReserved;
        public Point32 PtMaxSize;
        public Point32 PtMaxPosition;
        public Point32 PtMinTrackSize;
        public Point32 PtMaxTrackSize;
    }

    /// <summary>Call once the window's HWND exists - i.e. from
    /// <c>SourceInitialized</c>, not the constructor, which runs before the
    /// window has a native handle to hand these calls or to hook.</summary>
    public static void Apply(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || source.Handle == IntPtr.Zero)
            return;

        int dark = 1;
        DwmSetWindowAttribute(source.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        int round = DwmwcpRound;
        DwmSetWindowAttribute(source.Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));

        source.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // Both in physical pixels, same as the monitor rects GetMonitorInfo
        // returns - unlike WindowPlacement's own math this never has to
        // convert to DIPs, since WM_GETMINMAXINFO's struct is itself in
        // physical pixels regardless of the window's DPI.
        mmi.PtMaxPosition.X = info.RcWork.Left - info.RcMonitor.Left;
        mmi.PtMaxPosition.Y = info.RcWork.Top - info.RcMonitor.Top;
        mmi.PtMaxSize.X = info.RcWork.Right - info.RcWork.Left;
        mmi.PtMaxSize.Y = info.RcWork.Bottom - info.RcWork.Top;
        mmi.PtMaxTrackSize.X = mmi.PtMaxSize.X;
        mmi.PtMaxTrackSize.Y = mmi.PtMaxSize.Y;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
}
