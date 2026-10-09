using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static ScreenExtend.Native;

namespace ScreenExtend;

public enum MouseButton { Left = 0, Right = 1, Middle = 2, Back = 3, Forward = 4 }

/// <summary>Injects mouse and keyboard input with SendInput. Coordinates are physical virtual-desktop pixels.</summary>
public static class InputInjector
{
    private const int ChunkSize = 256;

    private static INPUT MouseIn(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    private static INPUT KeyIn(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private static void Send(List<INPUT> inputs)
    {
        for (int i = 0; i < inputs.Count; i += ChunkSize)
        {
            var chunk = inputs.GetRange(i, Math.Min(ChunkSize, inputs.Count - i)).ToArray();
            SendInput((uint)chunk.Length, chunk, InputSize);
        }
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, InputSize);

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Moves the cursor to an absolute physical pixel on the virtual desktop.</summary>
    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        x = Math.Clamp(x, vx, vx + vw - 1);
        y = Math.Clamp(y, vy, vy + vh - 1);
        // Windows maps normalized 0..65535 back to pixels as floor(n * size / 65536); use the ceiling inverse so the round trip is exact.
        int nx = (int)(((long)(x - vx) * 65536 + vw - 1) / vw);
        int ny = (int)(((long)(y - vy) * 65536 + vh - 1) / vh);
        Send(MouseIn(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public static void Button(MouseButton b, bool down)
    {
        uint flags, data = 0;
        switch (b)
        {
            case MouseButton.Right: flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case MouseButton.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MouseButton.Back: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case MouseButton.Forward: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
        }
        Send(MouseIn(flags, 0, 0, data));
    }

    /// <summary>Scrolls by <paramref name="delta"/> wheel units (120 = one notch). Vertical: positive scrolls up. Horizontal: positive scrolls right.</summary>
    public static void Wheel(int delta, bool horizontal)
    {
        if (delta == 0) return;
        Send(MouseIn(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta)));
    }

    // ------------------------------------------------------------------ keyboard

    public const ushort VK_SHIFT = 0xA0, VK_CONTROL = 0xA2, VK_MENU = 0xA4, VK_LWIN = 0x5B, VK_RETURN = 0x0D, VK_TAB = 0x09;

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Esc"] = 0x1B,
        ["Space"] = 0x20, ["Spacebar"] = 0x20, [" "] = 0x20,
        ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Shift"] = VK_SHIFT, ["Control"] = VK_CONTROL, ["Ctrl"] = VK_CONTROL, ["Alt"] = VK_MENU, ["AltGraph"] = 0xA5,
        ["Meta"] = VK_LWIN, ["Win"] = VK_LWIN, ["OS"] = VK_LWIN, ["Super"] = VK_LWIN,
        ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91, ["Pause"] = 0x13,
        ["PrintScreen"] = 0x2C, ["ContextMenu"] = 0x5D, ["Apps"] = 0x5D,
        ["AudioVolumeMute"] = 0xAD, ["AudioVolumeDown"] = 0xAE, ["AudioVolumeUp"] = 0xAF,
        ["MediaTrackNext"] = 0xB0, ["MediaTrackPrevious"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
        ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7, ["BrowserRefresh"] = 0xA8, ["BrowserHome"] = 0xAC,
    };

    private static bool IsExtended(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp PgDn End Home arrows
        0x2C or 0x2D or 0x2E => true, // PrintScreen Insert Delete
        0x5B or 0x5C or 0x5D => true, // Win keys, Apps
        0x6F or 0x90 => true, // Numpad divide, NumLock
        0xA3 or 0xA5 => true, // Right Ctrl, Right Alt
        >= 0xA6 and <= 0xB7 => true, // browser and media keys
        _ => false,
    };

    /// <summary>Resolves a key name (DOM KeyboardEvent.key style, e.g. "Enter", "F5", "a") to a virtual-key code.</summary>
    public static bool TryResolveKey(string? name, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrEmpty(name)) return false;
        if (NamedKeys.TryGetValue(name, out vk)) return true;
        if (name.Length >= 2 && name.Length <= 3 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int fn) && fn >= 1 && fn <= 24)
        {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'a' and <= 'z') { vk = (ushort)(c - 32); return true; }
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            short scan = VkKeyScanW(c);
            if (scan != -1) { vk = (ushort)(scan & 0xFF); return true; }
        }
        return false;
    }

    public static bool TryResolveModifier(string? name, out ushort vk)
    {
        vk = name?.ToLowerInvariant() switch
        {
            "ctrl" or "control" => VK_CONTROL,
            "shift" => VK_SHIFT,
            "alt" => VK_MENU,
            "meta" or "win" or "os" or "super" => VK_LWIN,
            _ => (ushort)0,
        };
        return vk != 0;
    }

    private static INPUT KeyEvent(ushort vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKeyW(vk, 0);
        uint flags = (down ? 0u : KEYEVENTF_KEYUP) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        return KeyIn(vk, scan, flags);
    }

    public static void Key(ushort vk, bool down) => Send(KeyEvent(vk, down));

    /// <summary>Presses modifiers, taps the key, then releases modifiers in reverse order, as one atomic SendInput batch.</summary>
    public static void KeyCombo(ushort vk, IReadOnlyList<ushort> modifiers)
    {
        var list = new List<INPUT>(modifiers.Count * 2 + 2);
        foreach (var m in modifiers) list.Add(KeyEvent(m, true));
        list.Add(KeyEvent(vk, true));
        list.Add(KeyEvent(vk, false));
        for (int i = modifiers.Count - 1; i >= 0; i--) list.Add(KeyEvent(modifiers[i], false));
        Send(list);
    }

    /// <summary>Types arbitrary Unicode text (independent of the PC keyboard layout). Newlines become Enter, tabs become Tab.</summary>
    public static void Text(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }
            if (c == '\n') { list.Add(KeyEvent(VK_RETURN, true)); list.Add(KeyEvent(VK_RETURN, false)); continue; }
            if (c == '\t') { list.Add(KeyEvent(VK_TAB, true)); list.Add(KeyEvent(VK_TAB, false)); continue; }
            if (char.IsControl(c)) continue;
            list.Add(KeyIn(0, c, KEYEVENTF_UNICODE));
            list.Add(KeyIn(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (list.Count > 0) Send(list);
    }
}

/// <summary>Phase of a touch contact reported by the device.</summary>
public enum TouchPhase { Down = 0, Move = 1, Up = 2 }

public readonly record struct TouchPoint(int Id, int X, int Y, TouchPhase Phase);

/// <summary>
/// Turns the device's fingers into real Windows touch contacts (InjectTouchInput), so apps on the virtual display get
/// native pinch, swipe, two-finger scroll and inking. One injector per client; contact slots are per process (max 10).
/// </summary>
public sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;
    private static readonly object InitGate = new();
    private static bool _initialized, _initFailed;
    private static readonly bool[] SlotsInUse = new bool[MaxContacts];

    private readonly object _gate = new();
    private readonly Dictionary<int, (int slot, int x, int y)> _held = new();
    private readonly Timer _keepAlive;
    private long _lastInject;

    public TouchInjector()
    {
        _keepAlive = new Timer(_ => KeepAlive(), null, 50, 50);
    }

    public static bool Available
    {
        get
        {
            lock (InitGate)
            {
                if (_initialized) return true;
                if (_initFailed) return false;
                _initialized = InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_DEFAULT);
                if (!_initialized)
                {
                    _initFailed = true;
                    Log.Warn("Touch injection is not available on this PC");
                }
                return _initialized;
            }
        }
    }

    /// <summary>Applies one batch of contact updates (screen pixel coordinates). Contacts not mentioned stay where they were.</summary>
    public bool Update(IReadOnlyList<TouchPoint> points)
    {
        if (!Available) return false;
        lock (_gate)
        {
            var frame = new List<POINTER_TOUCH_INFO>(_held.Count + points.Count);
            var mentioned = new HashSet<int>();
            foreach (var p in points)
            {
                mentioned.Add(p.Id);
                if (!_held.TryGetValue(p.Id, out var cur))
                {
                    if (p.Phase == TouchPhase.Up) continue;
                    int slot = AllocSlot();
                    if (slot < 0) continue;
                    cur = (slot, p.X, p.Y);
                    _held[p.Id] = cur;
                    frame.Add(Contact(slot, p.X, p.Y, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT));
                    continue;
                }
                if (p.Phase == TouchPhase.Up)
                {
                    frame.Add(Contact(cur.slot, p.X, p.Y, POINTER_FLAG_UP));
                    _held.Remove(p.Id);
                    FreeSlot(cur.slot);
                }
                else
                {
                    _held[p.Id] = (cur.slot, p.X, p.Y);
                    frame.Add(Contact(cur.slot, p.X, p.Y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT));
                }
            }
            foreach (var kv in _held)
                if (!mentioned.Contains(kv.Key))
                    frame.Add(Contact(kv.Value.slot, kv.Value.x, kv.Value.y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT));
            return Inject(frame);
        }
    }

    private void KeepAlive()
    {
        lock (_gate)
        {
            if (_held.Count == 0 || Environment.TickCount64 - _lastInject < 45) return;
            var frame = _held.Values.Select(v => Contact(v.slot, v.x, v.y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT)).ToList();
            Inject(frame);
        }
    }

    private bool Inject(List<POINTER_TOUCH_INFO> frame)
    {
        if (frame.Count == 0) return true;
        _lastInject = Environment.TickCount64;
        bool ok = InjectTouchInput((uint)frame.Count, frame.ToArray());
        if (!ok) Log.Debug($"InjectTouchInput failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return ok;
    }

    private static POINTER_TOUCH_INFO Contact(int slot, int x, int y, uint flags) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PT_TOUCH,
            pointerId = (uint)slot,
            pointerFlags = flags | (slot == 0 ? POINTER_FLAG_PRIMARY : 0),
            ptPixelLocation = new POINT { X = x, Y = y },
        },
        touchFlags = 0,
        touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
        rcContact = new RECT { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 },
        orientation = 90,
        pressure = 32000,
    };

    private static int AllocSlot()
    {
        lock (InitGate)
        {
            for (int i = 0; i < MaxContacts; i++)
                if (!SlotsInUse[i]) { SlotsInUse[i] = true; return i; }
            return -1;
        }
    }

    private static void FreeSlot(int slot)
    {
        lock (InitGate) SlotsInUse[slot] = false;
    }

    /// <summary>Lifts every finger this client still holds down (disconnect, mode change).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_held.Count == 0) return;
            var frame = _held.Values.Select(v => Contact(v.slot, v.x, v.y, POINTER_FLAG_UP)).ToList();
            foreach (var v in _held.Values) FreeSlot(v.slot);
            _held.Clear();
            if (_initialized) Inject(frame);
        }
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }
}

/// <summary>Tracks what one client is holding down so it can be released if the client disconnects mid-gesture.</summary>
public sealed class HeldInput
{
    private readonly HashSet<MouseButton> _buttons = new();
    private readonly HashSet<ushort> _keys = new();
    private readonly object _gate = new();

    public void Button(MouseButton b, bool down)
    {
        lock (_gate)
        {
            if (down) _buttons.Add(b); else _buttons.Remove(b);
        }
        InputInjector.Button(b, down);
    }

    public void Key(ushort vk, bool down)
    {
        lock (_gate)
        {
            if (down) _keys.Add(vk); else _keys.Remove(vk);
        }
        InputInjector.Key(vk, down);
    }

    public void ReleaseAll()
    {
        MouseButton[] buttons;
        ushort[] keys;
        lock (_gate)
        {
            buttons = new MouseButton[_buttons.Count];
            _buttons.CopyTo(buttons);
            keys = new ushort[_keys.Count];
            _keys.CopyTo(keys);
            _buttons.Clear();
            _keys.Clear();
        }
        foreach (var k in keys) InputInjector.Key(k, false);
        foreach (var b in buttons) InputInjector.Button(b, false);
    }
}
