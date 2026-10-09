using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ScreenExtend;

public enum DisplayPhase { None, Creating, Ready, Failed }

/// <summary>
/// One connected device (phone/tablet/browser). Owns the WebSocket, its virtual display (extend mode) or the monitor it
/// mirrors, sends frames with ack-based flow control (at most <see cref="MaxInFlight"/> unacknowledged frames, newest
/// frame wins), answers pings and injects input aimed at its display.
/// </summary>
public sealed class ClientSession : IFrameSink
{
    private const int MaxInFlight = 2;
    private const int MaxMessageBytes = 64 * 1024;
    private const long AckTimeoutMs = 3000;
    private const int SendTimeoutMs = 15000;
    private static int _nextId;

    private readonly WebSocket _ws;
    private readonly AppServer _app;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _displayLock = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly HeldInput _held = new();
    private readonly TouchInjector _touch = new();
    private readonly object _streamGate = new();
    private readonly CancellationTokenSource _cts = new();

    private EncodedFrame? _latest;
    private long _lastSentId;
    private int _inFlight;
    private long _inFlightSince = Environment.TickCount64;
    private volatile bool _wantStream;
    private volatile StreamSource? _source;
    private volatile string _device = "";
    private double _relX, _relY;
    private int _screenGen;

    // Device screen description (from hello / screen messages).
    private int _sw, _sh, _vw, _vh;
    private double _dpr = 1;
    private bool _portrait;
    private int _maxLong = DisplayRequest.DefaultMaxLongSide;
    private int _scale; // 0 = auto

    // Traffic counters (rates computed once per second by UpdateRates).
    private long _bytesSent, _framesSent, _lastBytes, _lastFrames;
    private long _lastRateTicks = Environment.TickCount64;

    public ClientSession(WebSocket ws, AppServer app, string remoteAddress)
    {
        _ws = ws;
        _app = app;
        RemoteAddress = remoteAddress;
    }

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public string Name { get; private set; } = "Device";
    public string RemoteAddress { get; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    /// <summary>"extend" (own virtual display) or "mirror" (an existing monitor).</summary>
    public string Mode { get; private set; } = "extend";
    /// <summary>Monitor index in mirror mode.</summary>
    public int Monitor { get; private set; }
    public string Device => _device;
    public StreamQuality Quality { get; private set; } = StreamQuality.Native;
    public bool Streaming => _wantStream;
    public bool Authorized { get; private set; }
    /// <summary>The paired device this session authenticated as (null until a valid token arrives).</summary>
    public PairedDevice? PairedDevice { get; private set; }
    public string ViewMode { get; private set; } = "touch";
    public VirtualDisplay? Display { get; private set; }
    public DisplayPhase Phase { get; private set; } = DisplayPhase.None;
    public string DisplayError { get; private set; } = "";

    // Client-reported stats.
    public double ClientFps { get; private set; }
    public double ClientLatencyMs { get; private set; }
    public double ClientRttMs { get; private set; }
    public string ClientQuality { get; private set; } = "";

    // Server-measured stats.
    public double SendKbps { get; private set; }
    public double SendFps { get; private set; }

    public bool CanControl => Authorized && _app.InputEnabled && (PairedDevice?.AllowControl ?? false);
    public StreamSource? Source => _source;

    // ------------------------------------------------------------------ frame sink

    public bool WantsFrame => _wantStream
        && (Volatile.Read(ref _inFlight) < MaxInFlight || Environment.TickCount64 - Volatile.Read(ref _inFlightSince) >= AckTimeoutMs);

    public void OnFrame(EncodedFrame frame)
    {
        Volatile.Write(ref _latest, frame);
        _wake.Writer.TryWrite(true);
    }

    private void OnAck()
    {
        int cur;
        do
        {
            cur = Volatile.Read(ref _inFlight);
            if (cur <= 0) break;
        } while (Interlocked.CompareExchange(ref _inFlight, cur - 1, cur) != cur);
        Volatile.Write(ref _inFlightSince, Environment.TickCount64);
        _wake.Writer.TryWrite(true);
        _source?.Wake();
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _cts.Token);
        var ct = linked.Token;
        var sendLoop = Task.Run(() => SendLoopAsync(ct), CancellationToken.None);
        try
        {
            await ReceiveLoopAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Log.Warn($"Session {Id} ({Name}) error: {ex.Message}");
        }
        finally
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
            StopStream();
            ReleaseInput();
            ReleaseDisplay();
            try { await sendLoop; } catch { /* already reported */ }
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(1000);
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                }
                catch { /* peer already gone */ }
            }
            _touch.Dispose();
        }
    }

    private void ReleaseInput()
    {
        _held.ReleaseAll();
        _touch.ReleaseAll();
    }

    private void ReleaseDisplay()
    {
        var vd = Display;
        if (vd == null) return;
        Display = null;
        Phase = DisplayPhase.None;
        _app.Displays.Release(vd);
    }

    /// <summary>Disconnects the client. <paramref name="reconnect"/> tells it whether to retry automatically; <paramref name="unpaired"/> that its token is no longer valid.</summary>
    public void Kick(string reason, bool reconnect, bool unpaired = false)
    {
        if (unpaired)
        {
            Authorized = false;
            StopStream();
            ReleaseInput();
        }
        _ = Task.Run(async () =>
        {
            try { await SendJsonAsync(new { t = "bye", reason, reconnect, unpaired }); } catch { }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        });
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var reader = _wake.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                reader.TryRead(out _);
                while (_wantStream && !ct.IsCancellationRequested)
                {
                    var f = Volatile.Read(ref _latest);
                    if (f == null || f.Id == _lastSentId) break;
                    int inFlight = Volatile.Read(ref _inFlight);
                    if (inFlight >= MaxInFlight)
                    {
                        // A client that stopped acknowledging (tab hidden, lost message) must not stall forever.
                        if (Environment.TickCount64 - Volatile.Read(ref _inFlightSince) < AckTimeoutMs) break;
                        Volatile.Write(ref _inFlight, 0);
                        inFlight = 0;
                    }
                    if (inFlight == 0) Volatile.Write(ref _inFlightSince, Environment.TickCount64);
                    Interlocked.Increment(ref _inFlight);
                    _lastSentId = f.Id;
                    await SendAsync(f.Data, WebSocketMessageType.Binary, ct);
                    Interlocked.Add(ref _bytesSent, f.Data.Length);
                    Interlocked.Increment(ref _framesSent);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Log.Warn($"Session {Id} send loop: {ex.Message}");
        }
        finally
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeoutMs); // a send stuck this long means the peer is gone
            await _ws.SendAsync(data, type, true, timeout.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendJsonAsync(object message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            await SendAsync(bytes, WebSocketMessageType.Text, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Fire-and-forget JSON send for callers on other threads.</summary>
    public void Post(object message) => _ = SendJsonAsync(message);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            ValueWebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxMessageBytes) throw new InvalidDataException("Message too large");
            } while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Text) continue;
            try
            {
                await HandleAsync(new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                Log.Debug($"Session {Id}: bad message ignored ({ex.Message})");
            }
        }
    }

    // ------------------------------------------------------------------ protocol

    private async Task HandleAsync(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        if (m.ValueKind != JsonValueKind.Object) return;
        string? t = m.Str("t");
        switch (t)
        {
            case "hello":
                await OnHelloAsync(m);
                return;
            case "ping":
                await SendJsonAsync(new { t = "pong", c = m.Num("c"), s = ServerClock.NowMs, sf = Math.Round(_source?.ProducedFps ?? 0, 1) });
                _wake.Writer.TryWrite(true); // periodic chance to recover from lost acks
                return;
            case "ack":
                OnAck();
                return;
        }

        if (!Authorized)
        {
            if (t != null) await SendJsonAsync(new { t = "unpaired" });
            return;
        }

        switch (t)
        {
            case "stream":
                if (QualityPreset.TryParse(m.Str("quality"), out var q)) Quality = q;
                if (Mode == "mirror" && m.Has("monitor")) Monitor = m.Int("monitor", Monitor);
                ApplyStream(m.Bool("on", true));
                await SendStateAsync();
                break;
            case "key": // the client lost sync (missed a full frame) and wants a keyframe
                _source?.RequestFrame();
                break;
            case "screen": // the device rotated or its window changed size
                ReadScreen(m);
                ScheduleReconfigure(400);
                break;
            case "display": // display settings changed on the device
            {
                string? mode = m.Str("mode");
                bool switchMode = mode is "extend" or "mirror" && mode != Mode;
                if (m.Has("monitor")) Monitor = m.Int("monitor", Monitor);
                if (m.Has("res")) _maxLong = Math.Clamp(m.Int("res", _maxLong), 0, 8192);
                if (m.Has("scale")) _scale = Math.Clamp(m.Int("scale", _scale), 0, 500);
                if (switchMode) Mode = mode!;
                _ = Task.Run(() => SetupDisplayAsync(reconfigure: true));
                break;
            }
            case "stats":
                ClientFps = m.Num("fps");
                ClientLatencyMs = m.Num("lat");
                ClientRttMs = m.Num("rtt");
                ClientQuality = Sanitize(m.Str("q"), 16);
                var mode2 = Sanitize(m.Str("mode"), 16);
                if (mode2.Length > 0 && mode2 != ViewMode)
                {
                    ViewMode = mode2;
                    if (mode2 != "native") _touch.ReleaseAll();
                }
                break;
            case "windows":
                await SendWindowsAsync();
                break;
            case "movewin":
            case "movefg":
            case "focuswin":
                if (CanControl) await HandleWindowCommandAsync(t, m);
                break;
            case "forget": // the user chose "Forget this PC" on the device
                if (PairedDevice != null && _app.Pairing.Revoke(PairedDevice.Id)) Log.Info($"Device '{Name}' unpaired itself");
                Authorized = false;
                StopStream();
                ReleaseInput();
                ReleaseDisplay();
                _app.NotifySessionsChanged();
                break;
            default:
                if (CanControl) HandleInput(t, m);
                break;
        }
    }

    private void ReadScreen(JsonElement m)
    {
        var s = m.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Object ? sc : m;
        _sw = Math.Clamp(s.Int("sw", _sw), 0, 16384);
        _sh = Math.Clamp(s.Int("sh", _sh), 0, 16384);
        _vw = Math.Clamp(s.Int("vw", _vw), 0, 16384);
        _vh = Math.Clamp(s.Int("vh", _vh), 0, 16384);
        double dpr = s.Num("dpr", _dpr);
        if (double.IsFinite(dpr) && dpr >= 0.5 && dpr <= 8) _dpr = dpr;
        _portrait = s.Bool("portrait", _vw > 0 && _vh > _vw);
        Interlocked.Increment(ref _screenGen);
    }

    private DisplayRequest BuildRequest()
    {
        int sw = _sw > 0 ? _sw : (_vw > 0 ? _vw : 1920), sh = _sh > 0 ? _sh : (_vh > 0 ? _vh : 1080);
        int longPx = (int)Math.Round(Math.Max(sw, sh) * _dpr), shortPx = (int)Math.Round(Math.Min(sw, sh) * _dpr);
        return new DisplayRequest(longPx, shortPx, _portrait, _maxLong, _scale);
    }

    private void ScheduleReconfigure(int delayMs)
    {
        int gen = Volatile.Read(ref _screenGen);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            if (Volatile.Read(ref _screenGen) != gen) return; // a newer screen report is pending
            await SetupDisplayAsync(reconfigure: true);
        });
    }

    private async Task OnHelloAsync(JsonElement m)
    {
        var device = _app.Pairing.Find(m.Str("token"));
        if (device == null)
        {
            // Not paired (or revoked): the client asks for the code shown on the PC, POSTs /api/pair and says hello again.
            await SendJsonAsync(new { t = "unpaired", host = Environment.MachineName });
            return;
        }
        bool first = !Authorized;
        PairedDevice = device;
        Name = Sanitize(m.Str("name"), 40) is { Length: > 0 } n ? n : device.Name;
        _app.Pairing.Touch(device, Name);
        Authorized = true;
        if (first) Log.Info($"Client {Id} ({Name}) connected from {RemoteAddress}");
        if (QualityPreset.TryParse(m.Str("quality"), out var q)) Quality = q;
        string? mode = m.Str("mode");
        Mode = mode == "mirror" ? "mirror" : "extend";
        Monitor = m.Int("monitor", 0);
        if (m.Has("res")) _maxLong = Math.Clamp(m.Int("res", _maxLong), 0, 8192);
        if (m.Has("scale")) _scale = Math.Clamp(m.Int("scale", _scale), 0, 500);
        ReadScreen(m);
        _wantStream = m.Bool("stream", true);

        var monitors = Monitors.GetAll();
        await SendJsonAsync(new
        {
            t = "welcome",
            v = 2,
            id = Id,
            host = Environment.MachineName,
            monitors = MonitorList(monitors),
            maxFps = _app.Hub.MaxFps,
            driver = DriverInfo(),
            touch = TouchInjector.Available,
            resolutions = DisplayRequest.ResolutionPresets,
            scales = DisplayLayout.Scales.Where(s => s <= 250).ToArray(),
        });
        _app.NotifySessionsChanged();
        _ = Task.Run(() => SetupDisplayAsync(reconfigure: false));
    }

    private object DriverInfo() => new
    {
        status = _app.Displays.Status switch { DriverStatus.Ok => "ok", DriverStatus.Error => "error", _ => "missing" },
        detail = _app.Displays.StatusDetail,
        connect = _app.ConnectPageUrl,
    };

    public static object[] MonitorList(List<MonitorInfo> monitors)
    {
        var list = new object[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            var mi = monitors[i];
            list[i] = new { i = mi.Index, label = mi.Label, w = mi.Width, h = mi.Height, primary = mi.Primary, @virtual = mi.Virtual, device = mi.Device };
        }
        return list;
    }

    /// <summary>Creates/configures the virtual display (extend) or picks the mirrored monitor, then (re)starts the stream.</summary>
    private async Task SetupDisplayAsync(bool reconfigure)
    {
        if (!Authorized) return;
        await _displayLock.WaitAsync();
        try
        {
            if (!Authorized) return;
            if (Mode == "extend")
            {
                if (_app.Displays.Status != DriverStatus.Ok) _app.Displays.Refresh();
                if (_app.Displays.Status != DriverStatus.Ok)
                {
                    // No driver: show the main display instead so the user still sees something, and say why.
                    ReleaseDisplay();
                    Phase = DisplayPhase.Failed;
                    DisplayError = _app.Displays.StatusDetail;
                    _device = Monitors.Primary().Device;
                }
                else
                {
                    try
                    {
                        if (Display == null || reconfigure)
                        {
                            Phase = DisplayPhase.Creating;
                            DisplayError = "";
                            await SendStateAsync();
                            var vd = Display ?? _app.Displays.Acquire(PairedDevice!.Id, Name);
                            Display = vd;
                            var mon = _app.Displays.Configure(vd, BuildRequest(), _dpr);
                            _device = vd.Device;
                            Phase = mon != null ? DisplayPhase.Ready : DisplayPhase.Failed;
                            if (mon == null) DisplayError = "The virtual display disappeared while it was being set up.";
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Virtual display for {Name}: {ex.Message}");
                        ReleaseDisplay();
                        Phase = DisplayPhase.Failed;
                        DisplayError = ex.Message;
                        _device = Monitors.Primary().Device;
                    }
                }
            }
            else
            {
                ReleaseDisplay();
                Phase = DisplayPhase.None;
                DisplayError = "";
                _device = Monitors.Get(Monitor).Device;
            }
            ApplyStream(_wantStream);
            await SendStateAsync();
        }
        finally
        {
            _displayLock.Release();
        }
        _app.NotifySessionsChanged();
    }

    private void ApplyStream(bool on)
    {
        lock (_streamGate)
        {
            if (Mode == "mirror")
            {
                int count = Monitors.GetAll().Count;
                Monitor = Monitor >= 0 && Monitor < count ? Monitor : 0;
                if (Display == null) _device = Monitors.Get(Monitor).Device;
            }
            if (string.IsNullOrEmpty(_device)) _device = Monitors.Primary().Device;
            if (on)
            {
                _wantStream = true;
                _source = _app.Hub.Subscribe(this, _source, _device, Quality);
            }
            else
            {
                _wantStream = false;
                _app.Hub.Unsubscribe(this, _source);
                _source = null;
            }
        }
        _app.NotifySessionsChanged();
    }

    private void StopStream()
    {
        lock (_streamGate)
        {
            _wantStream = false;
            _app.Hub.Unsubscribe(this, _source);
            _source = null;
        }
    }

    /// <summary>Called when remote control is allowed or blocked for this session.</summary>
    public void OnControlChanged()
    {
        if (!CanControl) ReleaseInput();
        if (Authorized) _ = SendStateAsync();
    }

    public Task SendStateAsync()
    {
        var src = _source;
        var all = Monitors.GetAll();
        // Fresh lookup first: the stream source refreshes its copy only once a second, too slow right after a mode change.
        var mon = all.FirstOrDefault(x => string.Equals(x.Device, _device, StringComparison.OrdinalIgnoreCase)) ?? src?.Monitor ?? all[0];
        var vd = Display;
        return SendJsonAsync(new
        {
            t = "state",
            streaming = _wantStream,
            hostStreaming = _app.Hub.HostStreaming,
            mode = Mode,
            monitor = all.FindIndex(x => string.Equals(x.Device, mon.Device, StringComparison.OrdinalIgnoreCase)),
            device = mon.Device,
            @virtual = vd != null && string.Equals(vd.Device, mon.Device, StringComparison.OrdinalIgnoreCase),
            quality = QualityPreset.Name(Quality),
            control = CanControl,
            blocked = src?.CaptureBlocked ?? false,
            missing = src?.MonitorMissing ?? false,
            mw = mon.Width,
            mh = mon.Height,
            monitors = MonitorList(all),
            display = new
            {
                phase = Phase.ToString().ToLowerInvariant(),
                error = DisplayError,
                w = vd?.Width ?? 0,
                h = vd?.Height ?? 0,
                scale = vd?.Scale ?? 0,
                portrait = vd?.Portrait ?? false,
                res = _maxLong,
                scalePref = _scale,
            },
            driver = DriverInfo(),
        });
    }

    // ------------------------------------------------------------------ windows (apps) on the device's display

    private async Task SendWindowsAsync()
    {
        var mon = CurrentMonitor();
        var list = await Task.Run(WindowMover.List);
        await SendJsonAsync(new
        {
            t = "windows",
            device = mon.Device,
            list = list.Select(w => new
            {
                h = w.Handle.ToString(),
                title = w.Title,
                proc = w.Process,
                here = string.Equals(w.Device, mon.Device, StringComparison.OrdinalIgnoreCase),
                min = w.Minimized,
                max = w.Maximized,
            }).ToArray(),
        });
    }

    private async Task HandleWindowCommandAsync(string t, JsonElement m)
    {
        var mon = CurrentMonitor();
        bool max = m.Bool("max", true);
        bool ok;
        if (t == "movefg")
            ok = WindowMover.MoveForegroundTo(mon, max);
        else
        {
            if (!long.TryParse(m.Str("h"), out long h) || h == 0) return;
            ok = t == "focuswin" ? WindowMover.Focus(new IntPtr(h)) : WindowMover.MoveTo(new IntPtr(h), mon, max);
        }
        if (!ok) await SendJsonAsync(new { t = "toast", text = t == "movefg" ? "No window to move. Click a window on the PC first." : "That window cannot be moved (closed, or elevated)." });
        else _source?.RequestFrame();
        await SendWindowsAsync();
    }

    // ------------------------------------------------------------------ input

    private MonitorInfo CurrentMonitor() => _source?.Monitor ?? Monitors.Find(_device) ?? Monitors.Primary();

    private static (int x, int y) ToScreen(MonitorInfo mon, double nx, double ny)
    {
        nx = double.IsFinite(nx) ? Math.Clamp(nx, 0, 1) : 0.5;
        ny = double.IsFinite(ny) ? Math.Clamp(ny, 0, 1) : 0.5;
        int x = mon.X + Math.Min(mon.Width - 1, (int)(nx * mon.Width));
        int y = mon.Y + Math.Min(mon.Height - 1, (int)(ny * mon.Height));
        return (x, y);
    }

    private static MouseButton ParseButton(JsonElement m) => m.Int("b", 0) switch
    {
        1 => MouseButton.Right,
        2 => MouseButton.Middle,
        3 => MouseButton.Back,
        4 => MouseButton.Forward,
        _ => MouseButton.Left,
    };

    private void HandleInput(string? t, JsonElement m)
    {
        var mon = CurrentMonitor();
        switch (t)
        {
            case "mm": // absolute move, normalized to the streamed monitor
            {
                var (x, y) = ToScreen(mon, m.Num("x"), m.Num("y"));
                InputInjector.MoveTo(x, y);
                break;
            }
            case "mr": // relative move in monitor pixels (trackpad mode)
            {
                double dx = m.Num("dx"), dy = m.Num("dy");
                if (!double.IsFinite(dx) || !double.IsFinite(dy)) break;
                _relX += Math.Clamp(dx, -5000, 5000);
                _relY += Math.Clamp(dy, -5000, 5000);
                int ix = (int)_relX, iy = (int)_relY;
                _relX -= ix;
                _relY -= iy;
                if (ix == 0 && iy == 0) break;
                var (cx, cy) = InputInjector.CursorPosition();
                // The pointer lives on this device's display: pull it over if it is somewhere else.
                int nx = Math.Clamp(cx + ix, mon.X, mon.X + mon.Width - 1);
                int ny = Math.Clamp(cy + iy, mon.Y, mon.Y + mon.Height - 1);
                InputInjector.MoveTo(nx, ny);
                break;
            }
            case "mb": // button down/up, optionally at a position
            {
                if (m.Has("x") && m.Has("y"))
                {
                    var (x, y) = ToScreen(mon, m.Num("x"), m.Num("y"));
                    InputInjector.MoveTo(x, y);
                }
                _held.Button(ParseButton(m), m.Bool("d", false));
                break;
            }
            case "mc": // click(s), optionally at a position
            {
                if (m.Has("x") && m.Has("y"))
                {
                    var (x, y) = ToScreen(mon, m.Num("x"), m.Num("y"));
                    InputInjector.MoveTo(x, y);
                }
                var b = ParseButton(m);
                int n = Math.Clamp(m.Int("n", 1), 1, 3);
                for (int i = 0; i < n; i++)
                {
                    _held.Button(b, true);
                    _held.Button(b, false);
                }
                break;
            }
            case "wh": // wheel, 120 units per notch
            {
                double dy = m.Num("dy"), dx = m.Num("dx");
                if (double.IsFinite(dy)) InputInjector.Wheel((int)Math.Clamp(Math.Round(dy), -2400, 2400), horizontal: false);
                if (double.IsFinite(dx)) InputInjector.Wheel((int)Math.Clamp(Math.Round(dx), -2400, 2400), horizontal: true);
                break;
            }
            case "tc": // native touch contacts: p = [{i, x, y, s}] with s 0 = down, 1 = move, 2 = up
            {
                if (!m.TryGetProperty("p", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
                var pts = new List<TouchPoint>(4);
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    int phase = e.Int("s", 1);
                    var (x, y) = ToScreen(mon, e.Num("x"), e.Num("y"));
                    pts.Add(new TouchPoint(e.Int("i", 0), x, y, phase == 0 ? TouchPhase.Down : phase == 2 ? TouchPhase.Up : TouchPhase.Move));
                    if (pts.Count >= 10) break;
                }
                if (pts.Count > 0) _touch.Update(pts);
                break;
            }
            case "kt": // type text
            {
                string? s = m.Str("s");
                if (!string.IsNullOrEmpty(s) && s.Length <= 4000) InputInjector.Text(s);
                break;
            }
            case "kk": // key press with optional modifiers
            {
                if (!InputInjector.TryResolveKey(m.Str("k"), out ushort vk)) break;
                var mods = new List<ushort>(4);
                if (m.TryGetProperty("m", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && InputInjector.TryResolveModifier(e.GetString(), out ushort mv) && !mods.Contains(mv) && mv != vk)
                            mods.Add(mv);
                }
                InputInjector.KeyCombo(vk, mods);
                break;
            }
            case "kd":
            case "ku":
            {
                if (InputInjector.TryResolveKey(m.Str("k"), out ushort vk)) _held.Key(vk, t == "kd");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ stats

    /// <summary>Recomputes send rates; called about once per second.</summary>
    public void UpdateRates()
    {
        long now = Environment.TickCount64;
        double dt = Math.Max(1, now - _lastRateTicks) / 1000.0;
        long bytes = Interlocked.Read(ref _bytesSent), frames = Interlocked.Read(ref _framesSent);
        SendKbps = (bytes - _lastBytes) * 8 / 1000.0 / dt;
        SendFps = (frames - _lastFrames) / dt;
        _lastBytes = bytes;
        _lastFrames = frames;
        _lastRateTicks = now;
    }

    public static string Sanitize(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = new List<char>(Math.Min(s.Length, max));
        foreach (char c in s)
        {
            if (char.IsControl(c)) continue;
            chars.Add(c);
            if (chars.Count >= max) break;
        }
        return new string(chars.ToArray()).Trim();
    }
}

internal static class JsonExt
{
    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out _);

    public static string? Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double Num(this JsonElement e, string name, double def = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : def;

    public static int Int(this JsonElement e, string name, int def = 0)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return def;
        if (v.TryGetInt32(out int i)) return i;
        return v.TryGetDouble(out double d) && double.IsFinite(d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : def;
    }

    public static bool Bool(this JsonElement e, string name, bool def = false) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def } : def;
}
