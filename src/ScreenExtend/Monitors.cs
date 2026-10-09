using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenExtend;

/// <summary>A monitor in virtual-desktop pixel coordinates (the process is per-monitor DPI aware). <see cref="Device"/> (e.g. \\.\DISPLAY3) is the stable key.</summary>
public sealed record MonitorInfo(int Index, string Device, int X, int Y, int Width, int Height, bool Primary)
{
    /// <summary>Work area (monitor minus taskbar), filled by <see cref="Monitors.GetAll"/>.</summary>
    public int WorkX { get; init; }
    public int WorkY { get; init; }
    public int WorkWidth { get; init; }
    public int WorkHeight { get; init; }
    /// <summary>True when this monitor is a virtual display created by this program.</summary>
    public bool Virtual { get; init; }

    public string Label => $"{(Virtual ? "Virtual display" : "Display")} {Index + 1}{(Primary ? " (main)" : "")} - {Width}x{Height}";
}

public static class Monitors
{
    /// <summary>Device names (\\.\DISPLAYn) of monitors created by the virtual display driver; set by <see cref="VirtualDisplayManager"/>.</summary>
    public static volatile HashSet<string> VirtualDevices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enumerates monitors. Primary first, then left-to-right, top-to-bottom. Index is the position in that order.</summary>
    public static List<MonitorInfo> GetAll()
    {
        var raw = new List<(string dev, Native.RECT r, Native.RECT work, bool primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT rc, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfoW(h, ref mi))
                raw.Add((mi.szDevice ?? "", mi.rcMonitor, mi.rcWork, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        var virt = VirtualDevices;
        var ordered = raw.OrderByDescending(m => m.primary).ThenBy(m => m.r.Left).ThenBy(m => m.r.Top).ToList();
        var list = new List<MonitorInfo>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (dev, r, work, primary) = ordered[i];
            list.Add(new MonitorInfo(i, dev, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary)
            {
                WorkX = work.Left, WorkY = work.Top, WorkWidth = work.Right - work.Left, WorkHeight = work.Bottom - work.Top,
                Virtual = virt.Contains(dev),
            });
        }
        if (list.Count == 0)
        {
            // Should never happen on an interactive desktop; fall back to the virtual screen.
            int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int w = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN)), hh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            list.Add(new MonitorInfo(0, "DISPLAY", x, y, w, hh, true) { WorkX = x, WorkY = y, WorkWidth = w, WorkHeight = hh });
        }
        return list;
    }

    public static MonitorInfo Get(int index)
    {
        var all = GetAll();
        return index >= 0 && index < all.Count ? all[index] : all[0];
    }

    /// <summary>The monitor with this device name, or null when it is not attached to the desktop (any more).</summary>
    public static MonitorInfo? Find(string? device)
    {
        if (string.IsNullOrEmpty(device)) return null;
        return GetAll().FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
    }

    public static MonitorInfo Primary() => GetAll()[0];

    /// <summary>Monitor containing a point, nearest when outside.</summary>
    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = GetAll();
        foreach (var m in all)
            if (x >= m.X && x < m.X + m.Width && y >= m.Y && y < m.Y + m.Height) return m;
        return all.OrderBy(m => Math.Abs(x - (m.X + m.Width / 2)) + Math.Abs(y - (m.Y + m.Height / 2))).First();
    }
}

/// <summary>Monotonic high resolution clock expressed as Unix epoch milliseconds (used for latency measurement).</summary>
public static class ServerClock
{
    private static readonly double BaseUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double NowMs => BaseUnixMs + Sw.Elapsed.TotalMilliseconds;
}
