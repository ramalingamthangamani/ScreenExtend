using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenExtend;

/// <summary>A top-level application window as shown to the device.</summary>
public sealed record WindowInfo(long Handle, string Title, string Process, string Device, bool Minimized, bool Maximized);

/// <summary>Lists application windows and moves them onto a given monitor (the phone's virtual display).</summary>
public static class WindowMover
{
    private static readonly int OwnPid = Environment.ProcessId;

    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        IntPtr shell = Native.GetShellWindow();
        var monitors = Monitors.GetAll();
        var procNames = new Dictionary<uint, string>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == shell || !Native.IsWindowVisible(hwnd)) return true;
                if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int len = Native.GetWindowTextLengthW(hwnd);
                if (len <= 0) return true;
                var buf = new char[Math.Min(len, 200) + 1];
                int n = Native.GetWindowTextW(hwnd, buf, buf.Length);
                string title = new string(buf, 0, Math.Max(0, n)).Trim();
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == OwnPid) return true;
                if (!procNames.TryGetValue(pid, out string? proc))
                {
                    try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; }
                    catch { proc = ""; }
                    procNames[pid] = proc;
                }
                if (string.Equals(proc, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || string.Equals(proc, "TextInputHost", StringComparison.OrdinalIgnoreCase))
                    proc = "";
                string device = "";
                if (Native.GetWindowRect(hwnd, out var r))
                {
                    var mon = Monitors.FromPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                    device = mon.Device;
                }
                result.Add(new WindowInfo(hwnd.ToInt64(), title, proc, device, Native.IsIconic(hwnd), Native.IsZoomed(hwnd)));
            }
            catch (Exception ex)
            {
                Log.Debug($"EnumWindows callback: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Moves a window onto <paramref name="mon"/> (centered, clamped to the work area) and optionally maximizes it there.</summary>
    public static bool MoveTo(IntPtr hwnd, MonitorInfo mon, bool maximize)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        bool wasMax = Native.IsZoomed(hwnd);
        if (Native.IsIconic(hwnd) || wasMax) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        int ww = mon.WorkWidth > 0 ? mon.WorkWidth : mon.Width;
        int wh = mon.WorkHeight > 0 ? mon.WorkHeight : mon.Height;
        int wx = mon.WorkWidth > 0 ? mon.WorkX : mon.X;
        int wy = mon.WorkHeight > 0 ? mon.WorkY : mon.Y;
        int w = Math.Clamp(r.Width, 200, ww);
        int h = Math.Clamp(r.Height, 120, wh);
        int x = wx + (ww - w) / 2;
        int y = wy + (wh - h) / 2;
        bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!ok) return false;
        if (maximize || wasMax) Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
        Native.SetForegroundWindow(hwnd);
        return true;
    }

    /// <summary>Moves the window the user is working in right now.</summary>
    public static bool MoveForegroundTo(MonitorInfo mon, bool maximize)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Native.GetShellWindow()) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == OwnPid) return false;
        return MoveTo(fg, mon, maximize);
    }

    public static bool Focus(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        return Native.SetForegroundWindow(hwnd);
    }
}
