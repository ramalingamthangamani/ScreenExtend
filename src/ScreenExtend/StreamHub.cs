using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ScreenExtend;

public enum StreamQuality { Low, Balanced, High, Native }

public readonly record struct QualityPreset(int MaxDim, int Jpeg, int Refine)
{
    public static QualityPreset For(StreamQuality q) => q switch
    {
        StreamQuality.Low => new QualityPreset(960, 50, 75),
        StreamQuality.High => new QualityPreset(1920, 75, 90),
        StreamQuality.Native => new QualityPreset(8192, 78, 92),
        _ => new QualityPreset(1280, 62, 85),
    };

    public static bool TryParse(string? s, out StreamQuality q)
    {
        switch (s?.ToLowerInvariant())
        {
            case "low": q = StreamQuality.Low; return true;
            case "balanced": q = StreamQuality.Balanced; return true;
            case "high": q = StreamQuality.High; return true;
            case "native": q = StreamQuality.Native; return true;
            default: q = StreamQuality.Balanced; return false;
        }
    }

    public static string Name(StreamQuality q) => q.ToString().ToLowerInvariant();
}

/// <summary>An encoded, ready-to-send binary frame (header + JPEG). Immutable once published.</summary>
public sealed class EncodedFrame
{
    /// <summary>
    /// Binary frame layout (little endian):
    ///  0 u8  type (1 = frame)        1 u8  flags (bit0 refined, bit1 cursor visible, bit2 partial)
    ///  2 u16 source id               4 u32 frame id
    ///  8 f64 capture time (server Unix ms)
    /// 16 u16 frame width            18 u16 frame height
    /// 20 f32 cursor x (normalized)  24 f32 cursor y (normalized)
    /// 28 u16 region x  30 u16 region y  32 u16 region width  34 u16 region height
    /// 36 ... JPEG bytes (the whole frame, or only the region when the partial flag is set)
    /// </summary>
    public const int HeaderSize = 36;
    public const byte FlagRefined = 1, FlagCursor = 2, FlagPartial = 4;

    public required long Id { get; init; }
    public required byte[] Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool Refined { get; init; }
    public required bool Partial { get; init; }
}

/// <summary>Receives frames from a <see cref="StreamSource"/>. Implementations must not block.</summary>
public interface IFrameSink
{
    /// <summary>True when the sink can accept another frame right now (flow control).</summary>
    bool WantsFrame { get; }
    void OnFrame(EncodedFrame frame);
}

/// <summary>
/// Captures one monitor at one quality preset on a dedicated thread and fans frames out to all subscribed sinks.
/// Captures only when at least one sink can accept a frame, sends only the changed rectangle when the change is
/// small, and sends a sharper full refined frame once the screen has been static for a moment.
/// </summary>
public sealed class StreamSource
{
    private const double RefineAfterMs = 350;
    private const double IdleAfterMs = 1500;
    private const int IdlePollMs = 66;
    /// <summary>Partial frames are used while the dirty rectangle covers less than this share of the frame.</summary>
    private const double PartialMaxShare = 0.55;
    private const int PartialPadding = 4;
    private static int _nextSourceId;

    private readonly StreamHub _hub;
    private readonly object _gate = new();
    private readonly List<IFrameSink> _sinks = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _forceFrame = true;
    private volatile EncodedFrame? _lastFull;
    private volatile MonitorInfo _monitor;

    public int SourceId { get; } = Interlocked.Increment(ref _nextSourceId) & 0xFFFF;
    public string Device { get; }
    public StreamQuality Quality { get; }
    public MonitorInfo Monitor => _monitor;

    // Statistics (read by the dashboard).
    public double ProducedFps { get; private set; }
    public double AvgCaptureMs { get; private set; }
    public double AvgEncodeMs { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public bool CaptureBlocked { get; private set; }
    /// <summary>True while the monitor is not attached to the desktop (virtual display being created or removed).</summary>
    public bool MonitorMissing { get; private set; }

    internal StreamSource(StreamHub hub, string device, StreamQuality quality)
    {
        _hub = hub;
        Device = device;
        Quality = quality;
        _monitor = Monitors.Find(device) ?? Monitors.Primary();
    }

    public int SinkCount { get { lock (_gate) return _sinks.Count; } }

    internal void Add(IFrameSink sink)
    {
        lock (_gate) if (!_sinks.Contains(sink)) _sinks.Add(sink);
        var last = _lastFull;
        if (last != null) sink.OnFrame(last);
        _forceFrame = true; // a full frame follows so partial updates since the last full frame are not missed
        Wake();
    }

    internal int Remove(IFrameSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
            return _sinks.Count;
        }
    }

    /// <summary>Wakes the capture thread (called on new demand, e.g. after a frame acknowledgement).</summary>
    public void Wake() => _wake.Set();

    /// <summary>Forces the next frame to be a full frame, sent even if the screen did not change.</summary>
    public void RequestFrame()
    {
        _forceFrame = true;
        Wake();
    }

    internal void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Capture {Device} {Quality}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    internal void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    private bool AnySinkReady()
    {
        lock (_gate)
        {
            foreach (var s in _sinks)
                if (s.WantsFrame) return true;
        }
        return false;
    }

    internal static (int w, int h) TargetSize(MonitorInfo mon, int maxDim)
    {
        int longSide = Math.Max(mon.Width, mon.Height);
        if (longSide <= maxDim) return (Math.Max(2, mon.Width & ~1), Math.Max(2, mon.Height & ~1));
        double s = (double)maxDim / longSide;
        int w = Math.Max(2, (int)Math.Round(mon.Width * s) & ~1);
        int h = Math.Max(2, (int)Math.Round(mon.Height * s) & ~1);
        return (w, h);
    }

    /// <summary>Pads a dirty rectangle to even coordinates (JPEG chroma blocks) and clamps it to the frame.</summary>
    internal static DirtyRect PadRegion(DirtyRect r, int w, int h, int pad = PartialPadding)
    {
        int x0 = Math.Max(0, (r.X - pad) & ~1);
        int y0 = Math.Max(0, (r.Y - pad) & ~1);
        int x1 = Math.Min(w, (r.X + r.Width + pad + 1) & ~1);
        int y1 = Math.Min(h, (r.Y + r.Height + pad + 1) & ~1);
        if (x1 <= x0) x1 = Math.Min(w, x0 + 2);
        if (y1 <= y0) y1 = Math.Min(h, y0 + 2);
        return new DirtyRect(x0, y0, x1 - x0, y1 - y0);
    }

    private void Run()
    {
        Native.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        var preset = QualityPreset.For(Quality);
        double nextMonitorCheck = 0, nextDue = 0, lastChange = 0, statsWindowStart = 0;
        bool refinedSent = true;
        int framesInWindow = 0;
        try
        {
            using var cap = new ScreenCapturer();
            while (!_stop)
            {
                if (!_hub.HostStreaming)
                {
                    _wake.WaitOne(250);
                    _forceFrame = true;
                    ProducedFps = 0;
                    continue;
                }

                double now = sw.Elapsed.TotalMilliseconds;
                if (now >= nextMonitorCheck)
                {
                    var found = Monitors.Find(Device);
                    bool missing = found == null;
                    if (found != null)
                    {
                        if (found.Width != _monitor.Width || found.Height != _monitor.Height) _forceFrame = true;
                        _monitor = found;
                    }
                    if (missing != MonitorMissing)
                    {
                        MonitorMissing = missing;
                        _forceFrame = true;
                        _hub.NotifyStateChanged();
                    }
                    nextMonitorCheck = now + 1000;
                }
                if (now - statsWindowStart >= 1000)
                {
                    ProducedFps = framesInWindow * 1000.0 / Math.Max(1, now - statsWindowStart);
                    framesInWindow = 0;
                    statsWindowStart = now;
                }

                if (MonitorMissing || !AnySinkReady())
                {
                    _wake.WaitOne(100);
                    continue;
                }

                if (now < nextDue)
                {
                    Thread.Sleep(Math.Max(1, (int)(nextDue - now)));
                    continue;
                }
                int interval = 1000 / Math.Clamp(_hub.MaxFps, 1, 60);
                // Poll less often once the screen has been static for a while (saves CPU; the first change still shows within ~66 ms).
                if (refinedSent && now - lastChange > IdleAfterMs) interval = Math.Max(interval, IdlePollMs);
                nextDue = now + interval;

                var mon = _monitor;
                var (tw, th) = TargetSize(mon, preset.MaxDim);
                double t0 = sw.Elapsed.TotalMilliseconds;
                double captureTime = ServerClock.NowMs;
                if (!cap.Capture(mon, tw, th, drawCursor: true))
                {
                    if (!CaptureBlocked) { CaptureBlocked = true; _hub.NotifyStateChanged(); }
                    Thread.Sleep(250);
                    continue;
                }
                if (CaptureBlocked) { CaptureBlocked = false; _forceFrame = true; _hub.NotifyStateChanged(); }
                double t1 = sw.Elapsed.TotalMilliseconds;
                AvgCaptureMs = AvgCaptureMs * 0.9 + (t1 - t0) * 0.1;

                var dirty = cap.Diff();
                bool force = _forceFrame;
                if (force) _forceFrame = false;
                bool changed = force || !dirty.IsEmpty;

                if (changed)
                {
                    lastChange = now;
                    refinedSent = false;
                    bool partial = !force && _lastFull != null && _lastFull.Width == tw && _lastFull.Height == th
                        && dirty.Area < PartialMaxShare * tw * th;
                    Publish(cap, partial ? PadRegion(dirty, tw, th) : new DirtyRect(0, 0, tw, th), partial, preset.Jpeg, captureTime, refined: false, sw);
                    framesInWindow++;
                }
                else if (!refinedSent && now - lastChange >= RefineAfterMs)
                {
                    refinedSent = true;
                    if (preset.Refine > preset.Jpeg)
                    {
                        Publish(cap, new DirtyRect(0, 0, tw, th), false, preset.Refine, captureTime, refined: true, sw);
                        framesInWindow++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Capture thread for {Device} crashed: {ex}");
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void Publish(ScreenCapturer cap, DirtyRect region, bool partial, int quality, double captureTime, bool refined, Stopwatch sw)
    {
        double e0 = sw.Elapsed.TotalMilliseconds;
        byte[] data = cap.EncodeRegion(region, quality, EncodedFrame.HeaderSize);
        AvgEncodeMs = AvgEncodeMs * 0.9 + (sw.Elapsed.TotalMilliseconds - e0) * 0.1;
        long id = _hub.NextFrameId();
        var span = data.AsSpan();
        span[0] = 1;
        span[1] = (byte)((refined ? EncodedFrame.FlagRefined : 0) | (cap.CursorVisible ? EncodedFrame.FlagCursor : 0) | (partial ? EncodedFrame.FlagPartial : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)SourceId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)id);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], captureTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)cap.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)cap.Height);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..], cap.CursorX);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..], cap.CursorY);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)region.X);
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)region.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)region.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)region.Height);

        var frame = new EncodedFrame { Id = id, Data = data, Width = cap.Width, Height = cap.Height, Refined = refined, Partial = partial };
        if (!partial) _lastFull = frame;
        LastFrameBytes = data.Length;
        FrameWidth = cap.Width;
        FrameHeight = cap.Height;
        IFrameSink[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var s in sinks)
        {
            try { s.OnFrame(frame); }
            catch (Exception ex) { Log.Error($"Frame sink failed: {ex.Message}"); }
        }
    }
}

/// <summary>Owns the stream sources. One source per (monitor device, quality) pair, shared by every client that asks for it.</summary>
public sealed class StreamHub
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, StreamQuality), StreamSource> _sources = new();
    private long _frameId;
    private volatile bool _hostStreaming = true;
    private volatile int _maxFps = 30;

    public event Action? StateChanged;

    public bool HostStreaming => _hostStreaming;
    public int MaxFps { get => _maxFps; set => _maxFps = Math.Clamp(value, 5, 60); }

    public long NextFrameId() => Interlocked.Increment(ref _frameId);

    public void SetHostStreaming(bool on)
    {
        if (_hostStreaming == on) return;
        _hostStreaming = on;
        foreach (var s in Sources) s.RequestFrame();
        NotifyStateChanged();
    }

    public void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Error($"StateChanged handler failed: {ex.Message}"); }
    }

    public StreamSource[] Sources { get { lock (_gate) return _sources.Values.ToArray(); } }

    /// <summary>Moves <paramref name="sink"/> from <paramref name="current"/> (if any) to the source for (device, quality).</summary>
    public StreamSource Subscribe(IFrameSink sink, StreamSource? current, string device, StreamQuality quality)
    {
        var key = (device.ToUpperInvariant(), quality);
        lock (_gate)
        {
            if (current != null && string.Equals(current.Device, device, StringComparison.OrdinalIgnoreCase) && current.Quality == quality
                && _sources.TryGetValue(key, out var same) && same == current)
            {
                current.Add(sink);
                return current;
            }
            if (current != null) RemoveLocked(sink, current);
            if (!_sources.TryGetValue(key, out var src))
            {
                src = new StreamSource(this, device, quality);
                _sources[key] = src;
                src.Start();
            }
            src.Add(sink);
            return src;
        }
    }

    public void Unsubscribe(IFrameSink sink, StreamSource? current)
    {
        if (current == null) return;
        lock (_gate) RemoveLocked(sink, current);
    }

    private void RemoveLocked(IFrameSink sink, StreamSource src)
    {
        var key = (src.Device.ToUpperInvariant(), src.Quality);
        if (src.Remove(sink) == 0 && _sources.TryGetValue(key, out var existing) && existing == src)
        {
            _sources.Remove(key);
            src.Stop();
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var s in _sources.Values) s.Stop();
            _sources.Clear();
        }
    }
}
