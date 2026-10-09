using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenExtend;

public enum DriverStatus { NotInstalled, Error, Ok }

/// <summary>
/// Thin wrapper around the Parsec Virtual Display Driver (an Indirect Display Driver signed by Parsec; the control
/// protocol is documented by the MIT-licensed parsec-vdd project). Displays are plugged and unplugged with IOCTLs and
/// the driver must be pinged at least every ~100 ms or it unplugs every display it created, which also cleans up after a crash.
/// </summary>
internal sealed class ParsecVdd : IDisposable
{
    public static readonly Guid AdapterGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    public const string AdapterName = "Parsec Virtual Display Adapter";
    public const string HardwareId = @"Root\Parsec\VDA";
    public const int MaxDisplays = 8;

    private const uint IoctlAdd = 0x0022e004;
    private const uint IoctlRemove = 0x0022a008;
    private const uint IoctlUpdate = 0x0022a00c;
    private const uint IoctlVersion = 0x0022e010;

    private readonly object _gate = new();
    private IntPtr _handle;

    private ParsecVdd(IntPtr handle) => _handle = handle;

    /// <summary>Opens the driver's control device. <paramref name="status"/> tells why when null is returned.</summary>
    public static ParsecVdd? TryOpen(out DriverStatus status, out string detail)
    {
        status = DriverStatus.NotInstalled;
        detail = "The virtual display driver is not installed.";
        var guid = AdapterGuid;
        IntPtr devInfo = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (devInfo == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetailW(devInfo, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(devInfo, ref data, buf, size, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(buf + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    IntPtr h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                        Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_OVERLAPPED | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
                    if (h != IntPtr.Zero && h != Native.INVALID_HANDLE_VALUE)
                    {
                        status = DriverStatus.Ok;
                        detail = "";
                        return new ParsecVdd(h);
                    }
                    status = DriverStatus.Error;
                    detail = $"The virtual display driver is installed but could not be opened (error {Marshal.GetLastWin32Error()}). Try restarting Windows.";
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return null;
    }

    public bool IsOpen => _handle != IntPtr.Zero;

    public int Version => IoControl(IoctlVersion, ReadOnlySpan<byte>.Empty);

    /// <summary>Keep-alive; must be called at least every 100 ms while displays exist.</summary>
    public void Update() => IoControl(IoctlUpdate, ReadOnlySpan<byte>.Empty);

    /// <summary>Plugs a new display. Returns its driver index (0-based) or -1.</summary>
    public int AddDisplay()
    {
        int idx = IoControl(IoctlAdd, ReadOnlySpan<byte>.Empty);
        Update();
        return idx;
    }

    public void RemoveDisplay(int index)
    {
        // The driver reads the index big-endian.
        ReadOnlySpan<byte> data = stackalloc byte[] { (byte)((index >> 8) & 0xFF), (byte)(index & 0xFF) };
        IoControl(IoctlRemove, data);
        Update();
    }

    private unsafe int IoControl(uint code, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return -1;
            byte* inBuf = stackalloc byte[32];
            new Span<byte>(inBuf, 32).Clear();
            data.Slice(0, Math.Min(data.Length, 32)).CopyTo(new Span<byte>(inBuf, 32));
            uint outBuf = 0;
            var ov = new Native.OVERLAPPED { hEvent = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero) };
            try
            {
                Native.DeviceIoControl(_handle, code, inBuf, 32, &outBuf, sizeof(uint), out _, ref ov);
                if (!Native.GetOverlappedResultEx(_handle, ref ov, out _, 5000, false)) return -1;
                return (int)outBuf;
            }
            finally
            {
                if (ov.hEvent != IntPtr.Zero) Native.CloseHandle(ov.hEvent);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero) Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>What a device would like its virtual display to look like.</summary>
public sealed record DisplayRequest(int PhoneLongPx, int PhoneShortPx, bool Portrait, int MaxLongSide, int ScalePercent)
{
    /// <summary>Resolution presets offered to the device: 0 = largest the driver supports.</summary>
    public static readonly int[] ResolutionPresets = { 1280, 1600, 1920, 2560, 3840 };
    public const int DefaultMaxLongSide = 1920;

    public static DisplayRequest Default => new(1920, 1080, false, DefaultMaxLongSide, 0);
}

public sealed record DisplayMode(int Width, int Height, int Hz)
{
    public double Aspect => (double)Math.Max(Width, Height) / Math.Max(1, Math.Min(Width, Height));
    public int Long => Math.Max(Width, Height);
}

/// <summary>Pure helpers for choosing a display mode and scale for a phone or tablet (unit tested).</summary>
public static class DisplayLayout
{
    public static readonly int[] Scales = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// <summary>
    /// Picks the landscape mode that best matches the device's aspect ratio without exceeding the requested long side
    /// (a 20:9 phone gets 2560x1080-class or 1920x1080-class modes, a 16:10 tablet 1920x1200 or 1280x800, ...).
    /// </summary>
    public static DisplayMode? ChooseMode(IReadOnlyCollection<DisplayMode> modes, DisplayRequest req)
    {
        if (modes.Count == 0) return null;
        int phoneLong = Math.Max(req.PhoneLongPx, req.PhoneShortPx), phoneShort = Math.Max(1, Math.Min(req.PhoneLongPx, req.PhoneShortPx));
        if (phoneLong < 320) { phoneLong = 1920; phoneShort = 1080; }
        double targetAspect = (double)phoneLong / phoneShort;
        int cap = req.MaxLongSide > 0 ? req.MaxLongSide : int.MaxValue;
        int targetLong = Math.Min(cap, Math.Max(phoneLong, 1280));

        var landscape = modes.Where(m => m.Width >= m.Height).ToList();
        if (landscape.Count == 0) landscape = modes.ToList();
        var within = landscape.Where(m => m.Long <= cap * 1.05).ToList();
        var pool = within.Count > 0 ? within : landscape;

        double Score(DisplayMode m) =>
            Math.Abs(Math.Log(m.Aspect / targetAspect)) * 4.0 // aspect mismatch costs letterboxing
            + Math.Abs(Math.Log((double)m.Long / targetLong)) // size mismatch costs sharpness or bandwidth
            + (m.Long < 1000 ? 0.5 : 0);

        return pool.OrderBy(Score).ThenByDescending(m => m.Hz == 60 ? 1000 : m.Hz > 60 ? 60 : m.Hz).First();
    }

    /// <summary>Default Windows scaling for the chosen mode on this device: Windows UI elements end up roughly phone sized.</summary>
    public static int AutoScale(DisplayMode mode, DisplayRequest req, double devicePixelRatio)
    {
        int shortSide = Math.Min(mode.Width, mode.Height);
        double cssShort = Math.Min(req.PhoneLongPx, req.PhoneShortPx) / Math.Max(1.0, devicePixelRatio);
        if (cssShort < 200) cssShort = 412; // unknown device: assume a phone
        double ideal = shortSide / cssShort * 100 * 0.8;
        return Snap((int)Math.Round(ideal));
    }

    public static int Snap(int percent)
    {
        int best = Scales[0];
        foreach (int s in Scales)
            if (Math.Abs(s - percent) < Math.Abs(best - percent)) best = s;
        return Math.Clamp(best, 100, 250);
    }
}

/// <summary>Per-monitor DPI scaling through the (undocumented but long-stable) call the Settings app uses, plus display-config helpers.</summary>
internal static class DisplayDpi
{
    /// <summary>One line per display path (active or not) with GDI source name and monitor name; for diagnostics.</summary>
    public static List<string> DescribePaths()
    {
        var lines = new List<string>();
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ALL_PATHS, out uint np, out uint nm) != 0) { lines.Add("GetDisplayConfigBufferSizes failed"); return lines; }
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[Math.Max(1, np)];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[Math.Max(1, nm)];
        int rc = Native.QueryDisplayConfig(Native.QDC_ALL_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
        if (rc != 0) { lines.Add($"QueryDisplayConfig failed {rc}"); return lines; }
        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER { type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = p.sourceAdapterId, id = p.sourceId },
                viewGdiDeviceName = "",
            };
            Native.DisplayConfigGetDeviceInfo(ref src);
            var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER { type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = p.targetAdapterId, id = p.targetId },
                monitorFriendlyDeviceName = "",
                monitorDevicePath = "",
            };
            Native.DisplayConfigGetDeviceInfo(ref tgt);
            if (p.targetAvailable == 0 && (p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) == 0 && string.IsNullOrEmpty(tgt.monitorFriendlyDeviceName)) continue;
            lines.Add($"{src.viewGdiDeviceName,-14} -> target {p.targetId} \"{tgt.monitorFriendlyDeviceName}\" available={p.targetAvailable} active={(p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0} inUse={(p.targetStatusFlags & Native.DISPLAYCONFIG_TARGET_IN_USE) != 0} tech={p.outputTechnology} {tgt.monitorDevicePath}");
        }
        return lines;
    }

    private static bool TryFindSource(string gdiDevice, out Native.LUID adapter, out uint sourceId)
    {
        adapter = default;
        sourceId = 0;
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return false;
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[Math.Max(1, nm)];
        if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
        for (int i = 0; i < np; i++)
        {
            var req = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceAdapterId,
                    id = paths[i].sourceId,
                },
                viewGdiDeviceName = "",
            };
            if (Native.DisplayConfigGetDeviceInfo(ref req) != 0) continue;
            if (string.Equals(req.viewGdiDeviceName, gdiDevice, StringComparison.OrdinalIgnoreCase))
            {
                adapter = paths[i].sourceAdapterId;
                sourceId = paths[i].sourceId;
                return true;
            }
        }
        return false;
    }

    /// <summary>Current scale in percent, or null when it cannot be read.</summary>
    public static int? Get(string gdiDevice)
    {
        if (!TryFindSource(gdiDevice, out var adapter, out uint id)) return null;
        var get = new Native.DISPLAYCONFIG_GET_DPI
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_DPI>(),
                adapterId = adapter,
                id = id,
            },
        };
        if (Native.DisplayConfigGetDeviceInfo(ref get) != 0) return null;
        // curScaleRel is relative to the recommended scale; min is relative too, and 100 % is always the minimum.
        int recommendedIdx = -get.minScaleRel;
        int curIdx = recommendedIdx + get.curScaleRel;
        return curIdx >= 0 && curIdx < DisplayLayout.Scales.Length ? DisplayLayout.Scales[curIdx] : null;
    }

    public static bool Set(string gdiDevice, int percent)
    {
        try
        {
            if (!TryFindSource(gdiDevice, out var adapter, out uint id)) return false;
            var get = new Native.DISPLAYCONFIG_GET_DPI
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                    size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_DPI>(),
                    adapterId = adapter,
                    id = id,
                },
            };
            if (Native.DisplayConfigGetDeviceInfo(ref get) != 0) return false;
            int recommendedIdx = -get.minScaleRel;
            int wantIdx = Array.IndexOf(DisplayLayout.Scales, DisplayLayout.Snap(percent));
            if (wantIdx < 0) return false;
            int rel = Math.Clamp(wantIdx - recommendedIdx, get.minScaleRel, get.maxScaleRel);
            if (rel == get.curScaleRel) return true;
            var set = new Native.DISPLAYCONFIG_SET_DPI
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Native.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                    size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SET_DPI>(),
                    adapterId = adapter,
                    id = id,
                },
                scaleRel = rel,
            };
            return Native.DisplayConfigSetDeviceInfo(ref set) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set scaling for {gdiDevice}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>A virtual monitor created for one device.</summary>
public sealed class VirtualDisplay
{
    internal int DriverIndex { get; init; }
    public string Device { get; init; } = "";
    /// <summary>Paired device id that currently uses (or recently used) this display.</summary>
    public string? Owner { get; internal set; }
    public string OwnerName { get; internal set; } = "";
    internal DateTime? ReleasedAt { get; set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public int Scale { get; internal set; }
    public bool Portrait { get; internal set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public bool InUse => Owner != null && ReleasedAt == null;
}

/// <summary>
/// Creates and configures virtual displays on demand: one per connected device, matched to the device's screen
/// (aspect ratio, orientation, resolution preset, scaling). A display released by a device that disconnects is kept
/// for a short grace period so a reconnecting phone finds its windows where it left them.
/// </summary>
public sealed class VirtualDisplayManager : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(25);
    private readonly object _gate = new();
    private readonly List<VirtualDisplay> _displays = new();
    private volatile ParsecVdd? _vdd;
    private Thread? _keepAlive;
    private Timer? _cleanup;
    private volatile bool _disposed;

    public DriverStatus Status { get; private set; } = DriverStatus.NotInstalled;
    public string StatusDetail { get; private set; } = "";
    public int DriverVersion { get; private set; }

    /// <summary>Raised (on a worker thread) when a display is created, configured or removed.</summary>
    public event Action? Changed;

    public VirtualDisplay[] Displays { get { lock (_gate) return _displays.ToArray(); } }

    public VirtualDisplayManager()
    {
        Refresh();
        _cleanup = new Timer(_ => Cleanup(), null, 5000, 5000);
    }

    /// <summary>Re-checks whether the driver is installed and opens it. Safe to call any time (e.g. after an install).</summary>
    public DriverStatus Refresh()
    {
        lock (_gate)
        {
            if (_vdd != null && _vdd.IsOpen) return Status;
            _vdd = ParsecVdd.TryOpen(out var status, out string detail);
            Status = status;
            StatusDetail = detail;
            if (_vdd != null)
            {
                DriverVersion = _vdd.Version;
                Log.Info($"Virtual display driver ready (Parsec VDD 0.{DriverVersion})");
                if (_keepAlive == null)
                {
                    _keepAlive = new Thread(KeepAliveLoop) { IsBackground = true, Name = "VDD keep-alive", Priority = ThreadPriority.AboveNormal };
                    _keepAlive.Start();
                }
            }
            else Log.Info($"Virtual display driver: {detail}");
            return Status;
        }
    }

    private void KeepAliveLoop()
    {
        // Never takes _gate: Acquire/Configure hold it for seconds while Windows attaches a display, and a pause in
        // pings longer than ~1 s would make the driver unplug every display. The ping itself is a cheap IOCTL.
        while (!_disposed)
        {
            var vdd = _vdd;
            if (vdd != null && vdd.IsOpen)
            {
                try { vdd.Update(); }
                catch (Exception ex) { Log.Warn($"VDD keep-alive failed: {ex.Message}"); }
            }
            Thread.Sleep(40);
        }
    }

    private static List<(string name, string adapter)> ActiveDisplayDevices()
    {
        var list = new List<(string, string)>();
        foreach (var d in AllDisplayDevices())
            if ((d.flags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && (d.flags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) == 0)
                list.Add((d.name, d.adapter));
        return list;
    }

    private static List<(string name, string adapter, uint flags, string id)> AllDisplayDevices()
    {
        var list = new List<(string, string, uint, string)>();
        var dd = new Native.DISPLAY_DEVICEW { cb = Marshal.SizeOf<Native.DISPLAY_DEVICEW>() };
        for (uint i = 0; Native.EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            list.Add((dd.DeviceName, dd.DeviceString, dd.StateFlags, dd.DeviceID));
            dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICEW>();
        }
        return list;
    }

    /// <summary>Diagnostic for --driver-test: plugs one display, prints what Windows reports for 12 s, unplugs it.</summary>
    public static int DriverTest(System.IO.TextWriter o)
    {
        var vdd = ParsecVdd.TryOpen(out var status, out string detail);
        o.WriteLine($"driver status: {status} {detail}");
        if (vdd == null) return 1;
        using (vdd)
        {
            o.WriteLine($"driver version: 0.{vdd.Version}");
            void Dump(string when)
            {
                o.WriteLine($"-- {when}: attached display devices");
                foreach (var d in AllDisplayDevices().Where(d => d.flags != 0)) o.WriteLine($"   {d.name}  flags=0x{d.flags:X}  \"{d.adapter}\"  {d.id}");
                o.WriteLine($"   monitors: {string.Join(", ", Monitors.GetAll().Select(m => $"{m.Device} {m.Width}x{m.Height}@{m.X},{m.Y}"))}");
                o.WriteLine("   display config paths (all):");
                foreach (var p in DisplayDpi.DescribePaths()) o.WriteLine("   " + p);
            }
            Dump("before");
            var ping = new Thread(() => { while (true) { try { vdd.Update(); } catch { return; } Thread.Sleep(40); } }) { IsBackground = true };
            ping.Start();
            int idx = vdd.AddDisplay();
            o.WriteLine($"AddDisplay -> {idx}");
            for (int t = 1; t <= 12; t++)
            {
                Thread.Sleep(1000);
                var mons = Monitors.GetAll();
                o.WriteLine($"t+{t}s: {mons.Count} monitor(s): {string.Join(", ", mons.Select(m => $"{m.Device} {m.Width}x{m.Height}"))}");
                if (t == 3 || t == 12) Dump($"t+{t}s");
                if (t == 2 && mons.Count == 1)
                {
                    int r = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_TOPOLOGY_EXTEND | Native.SDC_APPLY | Native.SDC_PATH_PERSIST_IF_REQUIRED);
                    o.WriteLine($"SetDisplayConfig(EXTEND) -> {r}");
                }
            }
            if (idx >= 0) vdd.RemoveDisplay(idx);
            Thread.Sleep(1500);
            Dump("after remove");
        }
        return 0;
    }

    private void PublishVirtualDevices()
    {
        Monitors.VirtualDevices = new HashSet<string>(_displays.Select(d => d.Device), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gives <paramref name="ownerId"/> a virtual display: the one it used before if still around, otherwise a new one.
    /// Throws <see cref="InvalidOperationException"/> with a user-readable message when that is not possible.
    /// </summary>
    public VirtualDisplay Acquire(string ownerId, string ownerName)
    {
        lock (_gate)
        {
            var mine = _displays.FirstOrDefault(d => d.Owner == ownerId);
            if (mine != null)
            {
                mine.ReleasedAt = null;
                mine.OwnerName = ownerName;
                return mine;
            }
            var free = _displays.Where(d => d.ReleasedAt != null).OrderBy(d => d.ReleasedAt).FirstOrDefault();
            if (free != null)
            {
                free.Owner = ownerId;
                free.OwnerName = ownerName;
                free.ReleasedAt = null;
                RaiseChanged();
                return free;
            }
            if (_vdd == null || !_vdd.IsOpen)
            {
                Refresh();
                if (_vdd == null) throw new InvalidOperationException(StatusDetail);
            }
            if (_displays.Count >= ParsecVdd.MaxDisplays) throw new InvalidOperationException($"At most {ParsecVdd.MaxDisplays} virtual displays are supported.");

            var before = ActiveDisplayDevices().Select(d => d.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int idx = _vdd.AddDisplay();
            if (idx < 0) throw new InvalidOperationException("The virtual display driver refused to add a display.");
            Log.Info($"Added virtual display (driver index {idx}) for {ownerName}");

            string? device = null;
            var started = DateTime.UtcNow;
            var deadline = started.AddSeconds(12);
            int extendAttempts = 0;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(120);
                var now = ActiveDisplayDevices();
                var fresh = now.Where(d => !before.Contains(d.name)).ToList();
                var parsec = fresh.FirstOrDefault(d => d.adapter.Contains("Parsec", StringComparison.OrdinalIgnoreCase));
                if (parsec.name != null) { device = parsec.name; break; }
                if (fresh.Count == 1) { device = fresh[0].name; break; }
                // Windows often connects the new monitor as a clone of the main display (or leaves it inactive) instead of
                // extending the desktop onto it. Ask for the extend topology; the monitor then shows up as its own \\.\DISPLAYn.
                double waited = (DateTime.UtcNow - started).TotalSeconds;
                if (waited > 1.5 * (extendAttempts + 1) && extendAttempts < 4)
                {
                    extendAttempts++;
                    int r = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_TOPOLOGY_EXTEND | Native.SDC_APPLY | Native.SDC_PATH_PERSIST_IF_REQUIRED);
                    Log.Debug($"SetDisplayConfig(extend) attempt {extendAttempts} -> {r}");
                }
            }
            if (device == null)
            {
                _vdd.RemoveDisplay(idx);
                throw new InvalidOperationException("Windows did not attach the new virtual display within 12 seconds. Check Display settings on the PC: the ParsecVDA monitor may be set to duplicate or be disconnected.");
            }
            var vd = new VirtualDisplay { DriverIndex = idx, Device = device, Owner = ownerId, OwnerName = ownerName };
            _displays.Add(vd);
            PublishVirtualDevices();
            Log.Info($"Virtual display {device} is attached");
            RaiseChanged();
            return vd;
        }
    }

    /// <summary>Lists the modes the driver offers for a display (landscape sizes, best refresh rate per size).</summary>
    public static List<DisplayMode> Modes(string device)
    {
        // Without EDS_ROTATEDMODE Windows lists modes in the display's current orientation (a portrait display reports
        // 1080x1920 with orientation 90), so normalize everything to landscape. Right after a display is attached the list
        // can still be empty for a moment, hence the retries.
        var best = new Dictionary<(int, int), int>();
        for (int attempt = 0; attempt < 10 && best.Count == 0; attempt++)
        {
            if (attempt > 0) Thread.Sleep(200);
            var dm = Native.DEVMODEW.Create();
            for (int i = 0; Native.EnumDisplaySettingsExW(device, i, ref dm, 0); i++)
            {
                int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
                bool ok = dm.dmBitsPerPel == 32 && w >= 480 && h >= 480;
                dm = Native.DEVMODEW.Create();
                if (!ok) continue;
                if (h > w) (w, h) = (h, w);
                var key = (w, h);
                if (!best.TryGetValue(key, out int cur) || Prefer(hz, cur)) best[key] = hz;
            }
        }
        return best.Select(kv => new DisplayMode(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

        static bool Prefer(int hz, int cur) => hz == 60 || (cur != 60 && hz > cur && hz <= 120);
    }

    /// <summary>Applies resolution, orientation and scale for the device's screen. Returns the resulting monitor (null when it vanished).</summary>
    public MonitorInfo? Configure(VirtualDisplay vd, DisplayRequest req, double devicePixelRatio)
    {
        lock (_gate)
        {
            var modes = Modes(vd.Device);
            var mode = DisplayLayout.ChooseMode(modes, req);
            if (mode == null)
            {
                Log.Warn($"{vd.Device}: no display modes reported");
                return Monitors.Find(vd.Device);
            }
            int w = req.Portrait ? mode.Height : mode.Width;
            int h = req.Portrait ? mode.Width : mode.Height;

            var cur = Native.DEVMODEW.Create();
            bool haveCur = Native.EnumDisplaySettingsExW(vd.Device, Native.ENUM_CURRENT_SETTINGS, ref cur, 0);
            uint orientation = req.Portrait ? Native.DMDO_90 : Native.DMDO_DEFAULT;
            bool sameMode = haveCur && cur.dmPelsWidth == w && cur.dmPelsHeight == h && cur.dmDisplayOrientation == orientation;
            if (!sameMode)
            {
                var dm = Native.DEVMODEW.Create();
                dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYORIENTATION | Native.DM_DISPLAYFREQUENCY | Native.DM_BITSPERPEL;
                dm.dmPelsWidth = (uint)w;
                dm.dmPelsHeight = (uint)h;
                dm.dmDisplayOrientation = orientation;
                dm.dmDisplayFrequency = (uint)mode.Hz;
                dm.dmBitsPerPel = 32;
                int r = Native.ChangeDisplaySettingsExW(vd.Device, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (r != Native.DISP_CHANGE_SUCCESSFUL)
                {
                    dm.dmFields &= ~Native.DM_DISPLAYFREQUENCY;
                    r = Native.ChangeDisplaySettingsExW(vd.Device, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
                }
                if (r != Native.DISP_CHANGE_SUCCESSFUL) Log.Warn($"{vd.Device}: could not set {w}x{h} (orientation {orientation}): ChangeDisplaySettingsEx returned {r}");
                else Log.Info($"{vd.Device}: {w}x{h} @ {mode.Hz} Hz{(req.Portrait ? " portrait" : "")}");
            }

            int scale = req.ScalePercent > 0 ? DisplayLayout.Snap(req.ScalePercent) : DisplayLayout.AutoScale(mode, req, devicePixelRatio);
            // The mode change settles asynchronously; give the desktop a moment before touching DPI.
            MonitorInfo? mon = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                mon = Monitors.Find(vd.Device);
                if (mon != null && (sameMode || (mon.Width == w && mon.Height == h))) break;
                Thread.Sleep(100);
            }
            if (DisplayDpi.Get(vd.Device) != scale)
            {
                if (!DisplayDpi.Set(vd.Device, scale)) Log.Warn($"{vd.Device}: could not set scaling to {scale}%");
            }
            vd.Width = w;
            vd.Height = h;
            vd.Scale = DisplayDpi.Get(vd.Device) ?? scale;
            Log.Info($"{vd.Device}: scaling {vd.Scale}%{(vd.Scale != scale ? $" (wanted {scale}%, Windows limits this resolution)" : "")}");
            vd.Portrait = req.Portrait;
            RaiseChanged();
            return mon ?? Monitors.Find(vd.Device);
        }
    }

    /// <summary>The device stopped using its display; it is removed after a grace period unless the device comes back.</summary>
    public void Release(VirtualDisplay vd)
    {
        lock (_gate)
        {
            if (!_displays.Contains(vd)) return;
            vd.ReleasedAt = DateTime.UtcNow;
        }
        RaiseChanged();
    }

    private void Cleanup()
    {
        List<VirtualDisplay> gone = new();
        lock (_gate)
        {
            foreach (var d in _displays)
                if (d.ReleasedAt != null && DateTime.UtcNow - d.ReleasedAt > Grace) gone.Add(d);
            foreach (var d in gone) RemoveLocked(d);
        }
        if (gone.Count > 0) RaiseChanged();
    }

    public void Remove(VirtualDisplay vd)
    {
        lock (_gate) RemoveLocked(vd);
        RaiseChanged();
    }

    private void RemoveLocked(VirtualDisplay vd)
    {
        if (!_displays.Remove(vd)) return;
        try { _vdd?.RemoveDisplay(vd.DriverIndex); }
        catch (Exception ex) { Log.Warn($"Removing virtual display failed: {ex.Message}"); }
        PublishVirtualDevices();
        Log.Info($"Removed virtual display {vd.Device}");
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            foreach (var d in _displays.ToArray()) RemoveLocked(d);
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error($"VirtualDisplayManager.Changed handler failed: {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanup?.Dispose();
        RemoveAll();
        lock (_gate)
        {
            _vdd?.Dispose();
            _vdd = null;
        }
    }
}
