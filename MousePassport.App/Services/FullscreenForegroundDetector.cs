using System.Runtime.InteropServices;
using MousePassport.App.Interop;

namespace MousePassport.App.Services;

internal static class FullscreenForegroundDetector
{
    private const int EdgeTolerancePx = 8;

    /// <summary>
    /// True when a visible, non-minimized foreground window from another process covers its monitor (typical fullscreen / borderless fullscreen).
    /// </summary>
    public static bool IsOtherProcessFullscreenForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return false;
        }

        return RectsMatchMonitor(windowRect, info.rcMonitor, EdgeTolerancePx);
    }

    private static bool RectsMatchMonitor(NativeMethods.RECT window, NativeMethods.RECT monitor, int tolerance)
    {
        return Within(window.Left, monitor.Left, tolerance) &&
               Within(window.Top, monitor.Top, tolerance) &&
               Within(window.Right, monitor.Right, tolerance) &&
               Within(window.Bottom, monitor.Bottom, tolerance);
    }

    private static bool Within(int a, int b, int tolerance)
    {
        return Math.Abs(a - b) <= tolerance;
    }
}
