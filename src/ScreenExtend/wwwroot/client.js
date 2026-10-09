/* ScreenExtend web client: shows the PC's virtual display made for this device and turns touch / mouse into input on it. */
(() => {
  'use strict';

  // ------------------------------------------------------------------ utilities
  const HEADER = 36;
  const $ = (id) => document.getElementById(id);
  const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
  const now = () => performance.timeOrigin + performance.now();
  const round = (v, d) => Math.round(v * 10 ** d) / 10 ** d;
  const store = {
    get(k, d) { try { const v = localStorage.getItem('se.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem('se.' + k, JSON.stringify(v)); } catch { /* private mode */ } },
  };

  const ICONS = {
    touch: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11V5.5a1.5 1.5 0 0 1 3 0V11"/><path d="M12 10.5V9a1.5 1.5 0 0 1 3 0v2"/><path d="M15 10.5a1.5 1.5 0 0 1 3 0V15a6 6 0 0 1-6 6h-1.2a5 5 0 0 1-3.9-1.9L4.6 16a1.6 1.6 0 0 1 2.4-2.1L9 15.5"/></svg>',
    trackpad: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3.5" y="4" width="17" height="16" rx="3"/><path d="M3.5 15.5h17M12 15.5V20"/></svg>',
    native: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M8 13V5a1.5 1.5 0 0 1 3 0v7"/><path d="M11 11a1.5 1.5 0 0 1 3 0v1"/><path d="M14 11.5a1.5 1.5 0 0 1 3 0V15a6 6 0 0 1-6 6h-1a5 5 0 0 1-4-2L3.8 15.6a1.5 1.5 0 0 1 2.3-1.9L8 15.5"/><path d="M3 5c1.2-1.6 3-2.5 5-2.5M16 2.5c2 0 3.8.9 5 2.5" opacity=".7"/></svg>',
    view: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 12s3.5-6.5 9.5-6.5S21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z"/><circle cx="12" cy="12" r="3"/></svg>',
    pause: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="5" width="4" height="14" rx="1.2"/><rect x="14" y="5" width="4" height="14" rx="1.2"/></svg>',
    play: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5.6v12.8a1 1 0 0 0 1.5.86l10.2-6.4a1 1 0 0 0 0-1.72L9.5 4.74A1 1 0 0 0 8 5.6Z"/></svg>',
    full: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 9V5a1 1 0 0 1 1-1h4M20 9V5a1 1 0 0 0-1-1h-4M4 15v4a1 1 0 0 0 1 1h4M20 15v4a1 1 0 0 1-1 1h-4"/></svg>',
    exitFull: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M9 4v4a1 1 0 0 1-1 1H4M15 4v4a1 1 0 0 0 1 1h4M9 20v-4a1 1 0 0 0-1-1H4M15 20v-4a1 1 0 0 1 1-1h4"/></svg>',
    keyboard: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="6" width="19" height="12" rx="2.5"/><path d="M6 10h.01M9.5 10h.01M13 10h.01M16.5 10h.01M6.5 14h11"/></svg>',
    settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h10M18 7h2M4 17h4M12 17h8"/><circle cx="16" cy="7" r="2"/><circle cx="10" cy="17" r="2"/></svg>',
    apps: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="11" height="9" rx="2"/><rect x="10" y="11" width="11" height="9" rx="2"/><path d="M14 8.5h4M18 8.5l-1.5-1.5M18 8.5 16.5 10"/></svg>',
    refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 12a8 8 0 1 1-2.3-5.7"/><path d="M20 4v5h-5"/></svg>',
  };
  const MODE_LABEL = {
    touch: 'Touch: tap to click, long-press for right click',
    trackpad: 'Trackpad: drag to move the pointer',
    native: 'Touchscreen: fingers act as real touch (pinch, swipe)',
    view: 'View only: input disabled, pinch to zoom',
  };

  // ------------------------------------------------------------------ elements & state
  const el = {
    stage: $('stage'), canvas: $('screen'), hud: $('hud'), hudToggle: $('hudToggle'),
    bars: document.querySelector('#hudStats .bars'), qdot: document.querySelector('.q-dot'),
    fps: $('statFps'), lat: $('statLat'), btnMode: $('btnMode'), btnApps: $('btnApps'), btnPause: $('btnPause'), btnFull: $('btnFull'),
    btnKbd: $('btnKbd'), btnSettings: $('btnSettings'), kbd: $('kbd'), keybar: $('keybar'),
    banner: $('banner'), bannerText: $('bannerText'), bannerAction: $('bannerAction'), bannerClose: $('bannerClose'),
    pairForm: $('pairForm'), pairCode: $('pairCode'), pairBtn: $('pairBtn'), pairMsg: $('pairMsg'),
    sheet: $('sheet'), setMode: $('setMode'), setRes: $('setRes'), setScale: $('setScale'), setQuality: $('setQuality'),
    setSens: $('setSens'), setName: $('setName'), setInfo: $('setInfo'), setForget: $('setForget'), setClose: $('setClose'),
    apps: $('apps'), appsList: $('appsList'), appsRefresh: $('appsRefresh'), appsMoveFg: $('appsMoveFg'), appsClose: $('appsClose'),
    zoomChip: $('zoomChip'), toast: $('toast'),
    overlay: $('overlay'), ovTitle: $('ovTitle'), ovText: $('ovText'), ovSpinner: $('ovSpinner'), ovAction: $('ovAction'),
  };
  const ctx2d = el.canvas.getContext('2d', { alpha: false, desynchronized: true });

  const S = {
    ws: null,
    connected: false,
    welcome: null,
    state: null,
    mode: store.get('mode', 'touch'),
    displayMode: store.get('displayMode', 'extend') === 'mirror' ? 'mirror' : 'extend',
    quality: store.get('quality', 'native'),
    monitor: store.get('monitor', 0),
    res: store.get('res', 1920),
    scale: store.get('scale', 0),
    paused: false,
    paired: false,      // the PC accepted our token
    stopped: false,     // disconnected by the PC: do not reconnect automatically
    pairing: false,     // pairing prompt is showing
    hidden: document.visibilityState === 'hidden',
    frameW: 0,
    frameH: 0,
    hasFrame: false,
    hasFull: false,
    lastKeyReq: 0,
    cursor: { x: 0.5, y: 0.5, visible: false },
    view: { s: 1, tx: 0, ty: 0 },
    layout: { left: 0, top: 0, w: 1, h: 1, W: 1, H: 1 },
    bannerDismissed: '',
  };
  const modes = () => (S.welcome && S.welcome.touch ? ['touch', 'native', 'trackpad', 'view'] : ['touch', 'trackpad', 'view']);
  if (!['touch', 'trackpad', 'native', 'view'].includes(S.mode)) S.mode = 'touch';

  // ------------------------------------------------------------------ stats & clock sync
  const clock = { samples: [], offset: 0 }; // offset = serverTime - clientTime
  const stats = { drawn: 0, fps: 0, lat: NaN, rtt: NaN, sourceFps: 0, lastFrameAt: 0, quality: 'none', windowStart: performance.now() };

  function onPong(m) {
    const t = now();
    const rtt = t - m.c;
    if (!(rtt >= 0 && rtt < 30000)) return;
    clock.samples.push({ rtt, off: m.s - (m.c + t) / 2 });
    if (clock.samples.length > 12) clock.samples.shift();
    clock.offset = clock.samples.reduce((a, b) => (b.rtt < a.rtt ? b : a)).off;
    stats.rtt = Number.isNaN(stats.rtt) ? rtt : stats.rtt * 0.7 + rtt * 0.3;
    stats.sourceFps = m.sf || 0;
  }

  function qualityLevel() {
    if (!S.connected) return 'none';
    const lat = Number.isNaN(stats.lat) ? stats.rtt : Math.max(stats.lat, stats.rtt);
    if (Number.isNaN(lat)) return 'none';
    const ratio = stats.sourceFps > 3 ? stats.fps / stats.sourceFps : 1;
    let score = lat < 60 ? 3 : lat < 120 ? 2 : lat < 250 ? 1 : 0;
    if (ratio < 0.5) score = Math.min(score, 1);
    else if (ratio < 0.8) score = Math.min(score, 2);
    return ['poor', 'fair', 'good', 'excellent'][score];
  }

  function updateStats() {
    const t = performance.now();
    const dt = (t - stats.windowStart) / 1000;
    if (dt >= 1) {
      stats.fps = stats.drawn / dt;
      stats.drawn = 0;
      stats.windowStart = t;
    }
    if (performance.now() - stats.lastFrameAt > 1500) stats.lat = NaN;
    stats.quality = qualityLevel();
    const idle = S.connected && stats.sourceFps < 0.5 && stats.fps < 0.5 && S.hasFrame && !S.paused;
    el.fps.textContent = S.connected ? (idle ? 'idle' : `${Math.round(stats.fps)} fps`) : '-- fps';
    const lat = Number.isNaN(stats.lat) ? stats.rtt : stats.lat;
    el.lat.textContent = S.connected && !Number.isNaN(lat) ? `${Math.round(lat)} ms` : '-- ms';
    el.bars.dataset.q = stats.quality;
    el.qdot.dataset.q = stats.quality;
    el.hud.title = `Quality: ${stats.quality} · round trip ${Number.isNaN(stats.rtt) ? '--' : Math.round(stats.rtt)} ms`;
  }

  // ------------------------------------------------------------------ connection
  let reconnectDelay = 400;
  let reconnectTimer = 0;
  let pingTimer = 0;

  function deviceName() {
    const saved = store.get('name', '');
    if (saved) return saved;
    const ua = navigator.userAgent;
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
    if (/iPhone/.test(ua)) return 'iPhone';
    const m = /Android[^;]*;\s*([^;)]+?)(?:\sBuild|\))/.exec(ua);
    if (m && m[1] && m[1].trim().length > 1) return m[1].trim();
    if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'Android phone' : 'Android tablet';
    if (/Windows/.test(ua)) return 'Windows browser';
    if (/Mac/.test(ua)) return 'Mac browser';
    return 'Browser';
  }

  function send(obj) {
    const ws = S.ws;
    if (ws && ws.readyState === 1) {
      ws.send(JSON.stringify(obj));
      return true;
    }
    return false;
  }

  /** Describes this device's screen so the PC can size and rotate the virtual display to match. */
  function screenInfo() {
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    return { sw: screen.width, sh: screen.height, vw, vh, dpr: devicePixelRatio || 1, portrait: vh > vw };
  }

  function sendHello() {
    send({
      t: 'hello', v: 2, token: store.get('token', ''), name: deviceName(), stream: wantStream(),
      mode: S.displayMode, monitor: S.monitor, quality: S.quality, res: S.res, scale: S.scale, screen: screenInfo(),
    });
  }

  function connect() {
    clearTimeout(reconnectTimer);
    S.stopped = false;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    let ws;
    try {
      ws = new WebSocket(`${proto}//${location.host}/ws`);
    } catch (e) {
      scheduleReconnect();
      return;
    }
    ws.binaryType = 'arraybuffer';
    S.ws = ws;
    if (!S.hasFrame) showOverlay('Connecting…', 'Looking for your PC on this network.', { spinner: true });

    ws.onopen = () => {
      reconnectDelay = 400;
      sendHello();
      clearInterval(pingTimer);
      ping();
      pingTimer = setInterval(ping, 1000);
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') {
        let m;
        try { m = JSON.parse(ev.data); } catch { return; }
        onMessage(m);
      } else {
        onFrame(ev.data);
      }
    };
    ws.onclose = () => {
      if (S.ws !== ws) return;
      S.ws = null;
      S.connected = false;
      S.paired = false;
      S.hasFull = false;
      clearInterval(pingTimer);
      cancelGesture();
      resetDecoder();
      updateStats();
      renderButtons();
      if (S.stopped || S.pairing) return;
      showOverlay('Reconnecting…', 'Lost connection to the PC. Retrying automatically.', { spinner: true });
      scheduleReconnect();
    };
    ws.onerror = () => { /* onclose follows */ };
  }

  function scheduleReconnect() {
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(Math.round(reconnectDelay * 1.6), 4000);
  }

  function ping() {
    send({ t: 'ping', c: now() });
  }

  function reportStats() {
    if (!S.connected) return;
    send({
      t: 'stats', fps: round(stats.fps, 1), lat: Number.isNaN(stats.lat) ? -1 : Math.round(stats.lat),
      rtt: Number.isNaN(stats.rtt) ? -1 : Math.round(stats.rtt), q: stats.quality, mode: S.mode,
    });
  }

  function wantStream() { return !S.paused && !S.hidden; }

  function requestStream() {
    send({ t: 'stream', on: wantStream(), monitor: S.monitor, quality: S.quality });
  }

  function sendDisplaySettings() {
    send({ t: 'display', mode: S.displayMode, monitor: S.monitor, res: S.res, scale: S.scale });
  }

  let screenTimer = 0;
  function reportScreen() {
    clearTimeout(screenTimer);
    screenTimer = setTimeout(() => {
      if (S.connected && S.displayMode === 'extend') send({ t: 'screen', screen: screenInfo() });
    }, 250);
  }

  function onMessage(m) {
    switch (m.t) {
      case 'welcome':
        S.welcome = m;
        S.connected = true;
        S.paired = true;
        S.pairing = false;
        if (!modes().includes(S.mode)) S.mode = 'touch';
        document.title = m.host ? `${m.host} · ScreenExtend` : 'ScreenExtend';
        updateStats();
        renderSettings();
        renderButtons();
        break;
      case 'unpaired':
        S.connected = false;
        S.paired = false;
        showPairing(m.host);
        break;
      case 'state': {
        const wasControl = S.state ? S.state.control : true;
        const prevDevice = S.state ? S.state.device : null;
        S.state = m;
        if (m.monitors && S.welcome) S.welcome.monitors = m.monitors;
        if (m.quality) S.quality = m.quality;
        if (m.mode) S.displayMode = m.mode;
        if (m.mode === 'mirror' && typeof m.monitor === 'number' && m.monitor >= 0) S.monitor = m.monitor;
        if (prevDevice && m.device && prevDevice !== m.device) { S.hasFull = false; resetView(); }
        if (wasControl && m.control === false) toast('Remote control is turned off on the PC');
        else if (wasControl === false && m.control) toast('Remote control is allowed again');
        if (!canControl()) closeKeyboard();
        refreshOverlay();
        renderBanner();
        renderSettings();
        break;
      }
      case 'pong':
        onPong(m);
        break;
      case 'windows':
        renderApps(m);
        break;
      case 'toast':
        if (m.text) toast(String(m.text), 2600);
        break;
      case 'bye':
        if (m.unpaired) store.set('token', '');
        if (m.reconnect === false) {
          S.stopped = true;
          clearTimeout(reconnectTimer);
          closeKeyboard();
          showOverlay('Disconnected', m.reason || 'The PC ended this session.', { action: { label: 'Reconnect', run: () => connect() } });
        } else {
          toast(m.reason || 'Disconnected by the PC');
        }
        break;
      default:
        break;
    }
  }

  // ------------------------------------------------------------------ frames
  let decoding = false;
  const queue = []; // frames waiting to be decoded, in arrival order

  function resetDecoder() {
    queue.length = 0;
  }

  function parseFrame(buf) {
    if (buf.byteLength <= HEADER) return null;
    const dv = new DataView(buf);
    if (dv.getUint8(0) !== 1) return null;
    return {
      flags: dv.getUint8(1),
      id: dv.getUint32(4, true),
      ts: dv.getFloat64(8, true),
      w: dv.getUint16(16, true),
      h: dv.getUint16(18, true),
      cx: dv.getFloat32(20, true),
      cy: dv.getFloat32(24, true),
      rx: dv.getUint16(28, true),
      ry: dv.getUint16(30, true),
      rw: dv.getUint16(32, true),
      rh: dv.getUint16(34, true),
      partial: (dv.getUint8(1) & 4) !== 0,
      jpeg: new Uint8Array(buf, HEADER),
    };
  }

  function ack(id) {
    send({ t: 'ack', id });
  }

  function onFrame(buf) {
    const f = parseFrame(buf);
    if (!f) return;
    // Partial frames must be drawn in order, but a full frame makes everything queued before it irrelevant.
    if (!f.partial) {
      for (const q of queue) ack(q.id);
      queue.length = 0;
    }
    queue.push(f);
    if (!decoding) pump();
  }

  async function decodeToDrawable(bytes) {
    const blob = new Blob([bytes], { type: 'image/jpeg' });
    if (typeof createImageBitmap === 'function') return createImageBitmap(blob);
    const url = URL.createObjectURL(blob);
    try {
      const img = new Image();
      img.src = url;
      await img.decode();
      return img;
    } finally {
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  }

  async function pump() {
    decoding = true;
    while (queue.length) {
      const f = queue.shift();
      try {
        const img = await decodeToDrawable(f.jpeg);
        draw(img, f);
        if (img.close) img.close();
      } catch (e) {
        /* corrupt frame: skip */
      }
      ack(f.id);
    }
    decoding = false;
  }

  function requestKeyframe() {
    const t = performance.now();
    if (t - S.lastKeyReq < 800) return;
    S.lastKeyReq = t;
    send({ t: 'key' });
  }

  function draw(img, f) {
    if (f.w !== S.frameW || f.h !== S.frameH) {
      if (f.partial) { requestKeyframe(); return; } // size changed: wait for a full frame
      S.frameW = f.w;
      S.frameH = f.h;
      el.canvas.width = f.w;
      el.canvas.height = f.h;
      S.hasFull = false;
      layout();
    }
    if (f.partial) {
      if (!S.hasFull) { requestKeyframe(); return; }
      ctx2d.drawImage(img, f.rx, f.ry, f.rw, f.rh);
    } else {
      ctx2d.drawImage(img, 0, 0, f.w, f.h);
      S.hasFull = true;
    }
    S.cursor.x = f.cx;
    S.cursor.y = f.cy;
    S.cursor.visible = (f.flags & 2) !== 0;
    if (!S.hasFrame) {
      S.hasFrame = true;
      el.canvas.classList.add('live');
      refreshOverlay();
    }
    stats.drawn++;
    stats.lastFrameAt = performance.now();
    if (clock.samples.length) {
      const lat = now() + clock.offset - f.ts;
      if (lat > -50 && lat < 20000) stats.lat = Number.isNaN(stats.lat) ? Math.max(0, lat) : stats.lat * 0.8 + Math.max(0, lat) * 0.2;
    }
    if (S.mode === 'trackpad' && S.view.s > 1.01) followCursor();
  }

  // ------------------------------------------------------------------ layout & view transform
  function layout() {
    const W = el.stage.clientWidth || window.innerWidth;
    const H = el.stage.clientHeight || window.innerHeight;
    const fw = S.frameW || 16;
    const fh = S.frameH || 9;
    const s = Math.min(W / fw, H / fh);
    const w = fw * s;
    const h = fh * s;
    S.layout = { left: (W - w) / 2, top: (H - h) / 2, w, h, W, H };
    el.canvas.style.left = `${S.layout.left}px`;
    el.canvas.style.top = `${S.layout.top}px`;
    el.canvas.style.width = `${w}px`;
    el.canvas.style.height = `${h}px`;
    clampView();
    applyView();
  }

  function clampView() {
    const L = S.layout;
    const v = S.view;
    v.s = clamp(v.s, 1, 8);
    const w = L.w * v.s;
    const h = L.h * v.s;
    v.tx = w <= L.W ? (L.W - w) / 2 - L.left : clamp(v.tx, L.W - w - L.left, -L.left);
    v.ty = h <= L.H ? (L.H - h) / 2 - L.top : clamp(v.ty, L.H - h - L.top, -L.top);
  }

  function applyView() {
    const v = S.view;
    el.canvas.style.transform = `translate(${v.tx}px, ${v.ty}px) scale(${v.s})`;
    const zoomed = v.s > 1.01;
    el.zoomChip.classList.toggle('hidden', !zoomed);
    if (zoomed) el.zoomChip.textContent = `${v.s.toFixed(1)}× · reset`;
  }

  function resetView() {
    S.view = { s: 1, tx: 0, ty: 0 };
    clampView();
    applyView();
  }

  function zoomAround(s0, tx0, ty0, cx0, cy0, s1, cx, cy) {
    const L = S.layout;
    const px = (cx0 - L.left - tx0) / s0;
    const py = (cy0 - L.top - ty0) / s0;
    S.view.s = clamp(s1, 1, 8);
    S.view.tx = cx - L.left - px * S.view.s;
    S.view.ty = cy - L.top - py * S.view.s;
    clampView();
    applyView();
  }

  function followCursor() {
    const L = S.layout;
    const v = S.view;
    const x = L.left + v.tx + S.cursor.x * L.w * v.s;
    const y = L.top + v.ty + S.cursor.y * L.h * v.s;
    const mx = L.W * 0.15;
    const my = L.H * 0.15;
    let dx = 0;
    let dy = 0;
    if (x < mx) dx = mx - x; else if (x > L.W - mx) dx = L.W - mx - x;
    if (y < my) dy = my - y; else if (y > L.H - my) dy = L.H - my - y;
    if (dx || dy) {
      v.tx += dx;
      v.ty += dy;
      clampView();
      applyView();
    }
  }

  /** Stage client coordinates -> normalized remote coordinates (0..1). */
  function toRemote(clientX, clientY) {
    const r = el.canvas.getBoundingClientRect();
    return {
      x: clamp((clientX - r.left) / Math.max(1, r.width), 0, 1),
      y: clamp((clientY - r.top) / Math.max(1, r.height), 0, 1),
    };
  }

  // ------------------------------------------------------------------ outgoing input (coalesced once per animation frame)
  const out = { abs: null, rx: 0, ry: 0, wx: 0, wy: 0, touches: new Map(), raf: 0 };

  function schedule() {
    if (!out.raf) out.raf = requestAnimationFrame(flush);
  }

  function flush() {
    if (out.raf) cancelAnimationFrame(out.raf);
    out.raf = 0;
    if (out.abs) {
      send({ t: 'mm', x: round(out.abs.x, 5), y: round(out.abs.y, 5) });
      out.abs = null;
    }
    if (out.rx || out.ry) {
      send({ t: 'mr', dx: round(out.rx, 2), dy: round(out.ry, 2) });
      out.rx = 0;
      out.ry = 0;
    }
    const wx = Math.trunc(out.wx);
    const wy = Math.trunc(out.wy);
    if (wx || wy) {
      send({ t: 'wh', dx: wx, dy: wy });
      out.wx -= wx;
      out.wy -= wy;
    }
    if (out.touches.size) {
      send({ t: 'tc', p: [...out.touches.values()] });
      out.touches.clear();
    }
  }

  const input = {
    moveTo(p) { out.abs = p; schedule(); },
    moveBy(dx, dy) { out.rx += dx; out.ry += dy; schedule(); },
    wheel(dx, dy) { out.wx += dx; out.wy += dy; schedule(); },
    now(msg) { flush(); send(msg); },
    button(b, down, p) { input.now(p ? { t: 'mb', b, d: down, x: round(p.x, 5), y: round(p.y, 5) } : { t: 'mb', b, d: down }); },
    click(b, p, n = 1) { input.now(p ? { t: 'mc', b, n, x: round(p.x, 5), y: round(p.y, 5) } : { t: 'mc', b, n }); },
    /** Native touch: s = 0 down, 1 move, 2 up. Downs and ups go out at once; moves are coalesced per frame. */
    touch(id, p, s) {
      const pt = { i: id, x: round(p.x, 5), y: round(p.y, 5), s };
      if (s === 1) { out.touches.set(id, pt); schedule(); return; }
      out.touches.delete(id);
      flush();
      send({ t: 'tc', p: [pt] });
    },
  };

  function canControl() {
    return S.connected && S.mode !== 'view' && (!S.state || S.state.control !== false);
  }

  // ------------------------------------------------------------------ gestures
  const TAP_SLOP = 10;
  const LONG_PRESS_MS = 500;
  const TWO_TAP_MS = 320;
  const PINCH_THRESHOLD = 22;
  const SCROLL_THRESHOLD = 10;
  const WHEEL_PER_PX = 2.4;

  const pointers = new Map();
  const nativeIds = new Map(); // pointerId -> small contact id for native touch
  let g = null;
  let mouseDown = null;

  function haptic(ms) {
    if (navigator.vibrate) {
      try { navigator.vibrate(ms); } catch { /* ignore */ }
    }
  }

  function ripple(x, y, right) {
    const d = document.createElement('div');
    d.className = right ? 'ripple right' : 'ripple';
    d.style.left = `${x}px`;
    d.style.top = `${y}px`;
    document.body.appendChild(d);
    setTimeout(() => d.remove(), 400);
  }

  function centroid() {
    let x = 0;
    let y = 0;
    for (const p of pointers.values()) { x += p.x; y += p.y; }
    const n = Math.max(1, pointers.size);
    return { x: x / n, y: y / n };
  }

  function spread() {
    const ps = [...pointers.values()];
    if (ps.length < 2) return 0;
    return Math.hypot(ps[0].x - ps[1].x, ps[0].y - ps[1].y);
  }

  function cancelGesture() {
    if (g && g.timer) clearTimeout(g.timer);
    if (g && g.buttonDown) input.button(0, false);
    if (mouseDown) {
      input.button(mouseDown.b, false);
      mouseDown = null;
    }
    for (const [pid, cid] of nativeIds) {
      const p = pointers.get(pid);
      input.touch(cid, p ? toRemote(p.x, p.y) : { x: 0.5, y: 0.5 }, 2);
    }
    nativeIds.clear();
    g = null;
    pointers.clear();
  }

  function allocNativeId(pointerId) {
    const used = new Set(nativeIds.values());
    let id = 0;
    while (used.has(id)) id++;
    nativeIds.set(pointerId, id);
    return id;
  }

  function startSingle(e) {
    const p = { x: e.clientX, y: e.clientY };
    g = { kind: 'one', phase: 'pending', t0: performance.now(), x0: p.x, y0: p.y, lx: p.x, ly: p.y, lt: performance.now(), buttonDown: false, timer: 0 };
    if (S.mode === 'view') return;
    g.timer = setTimeout(() => {
      if (!g || g.kind !== 'one' || g.phase !== 'pending' || !canControl()) return;
      if (S.mode === 'touch') {
        input.click(1, toRemote(g.x0, g.y0));
        ripple(g.x0, g.y0, true);
        g.phase = 'done';
      } else {
        input.button(0, true); // trackpad: press-and-hold starts a drag
        g.buttonDown = true;
        g.phase = 'held';
      }
      haptic(15);
    }, LONG_PRESS_MS);
  }

  function startDouble() {
    if (g && g.timer) clearTimeout(g.timer);
    if (g && g.buttonDown) input.button(0, false);
    const c = centroid();
    const v = S.view;
    g = {
      kind: 'two', phase: 'undecided', t0: performance.now(), moved: false,
      d0: spread(), c0: c, lc: c, s0: v.s, tx0: v.tx, ty0: v.ty, buttonDown: false, timer: 0,
    };
  }

  function onPointerDown(e) {
    if (e.pointerType === 'mouse' || e.pointerType === 'pen') { onMouseDown(e); return; }
    e.preventDefault();
    try { el.stage.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY, t: performance.now() });
    if (S.mode === 'native') {
      if (!canControl()) return;
      const id = allocNativeId(e.pointerId);
      input.touch(id, toRemote(e.clientX, e.clientY), 0);
      return;
    }
    if (pointers.size === 1) startSingle(e);
    else if (pointers.size === 2) startDouble();
    else if (g) {
      if (g.timer) clearTimeout(g.timer);
      g = { kind: 'many', buttonDown: false, timer: 0 };
    }
  }

  function onPointerMove(e) {
    if (e.pointerType === 'mouse' || e.pointerType === 'pen') { onMouseMove(e); return; }
    const p = pointers.get(e.pointerId);
    if (!p) return;
    e.preventDefault();
    p.x = e.clientX;
    p.y = e.clientY;
    if (S.mode === 'native') {
      const id = nativeIds.get(e.pointerId);
      if (id !== undefined && canControl()) input.touch(id, toRemote(p.x, p.y), 1);
      return;
    }
    if (!g) return;

    if (g.kind === 'one') {
      const dist = Math.hypot(p.x - g.x0, p.y - g.y0);
      if (S.mode === 'view') {
        S.view.tx += p.x - g.lx;
        S.view.ty += p.y - g.ly;
        clampView();
        applyView();
      } else if (S.mode === 'touch') {
        if (g.phase === 'pending' && dist > TAP_SLOP && canControl()) {
          clearTimeout(g.timer);
          g.phase = 'drag';
          input.button(0, true, toRemote(g.x0, g.y0));
          g.buttonDown = true;
        }
        if (g.phase === 'drag') input.moveTo(toRemote(p.x, p.y));
      } else if (S.mode === 'trackpad') {
        if (g.phase === 'pending' && dist > TAP_SLOP) {
          clearTimeout(g.timer);
          g.phase = 'move';
        }
        if ((g.phase === 'move' || g.phase === 'held') && canControl()) {
          const t = performance.now();
          const dx = p.x - g.lx;
          const dy = p.y - g.ly;
          const dt = Math.max(1, t - g.lt);
          const speed = Math.hypot(dx, dy) / dt;
          const gain = trackpadGain() * (0.5 + Math.min(2, speed * 1.2));
          input.moveBy(dx * gain, dy * gain);
          g.lt = t;
        }
      }
      g.lx = p.x;
      g.ly = p.y;
      return;
    }

    if (g.kind === 'two' && pointers.size >= 2) {
      const c = centroid();
      const d = spread();
      const zoomed = S.view.s > 1.01;
      if (g.phase === 'undecided') {
        if (Math.abs(d - g.d0) > PINCH_THRESHOLD || (zoomed && Math.hypot(c.x - g.c0.x, c.y - g.c0.y) > SCROLL_THRESHOLD) || S.mode === 'view') {
          g.phase = 'pinch';
          g.d0 = d;
          g.c0 = c;
          g.s0 = S.view.s;
          g.tx0 = S.view.tx;
          g.ty0 = S.view.ty;
        } else if (Math.hypot(c.x - g.c0.x, c.y - g.c0.y) > SCROLL_THRESHOLD) {
          g.phase = 'scroll';
          g.lc = c;
          if (S.mode === 'touch' && canControl()) input.moveTo(toRemote(c.x, c.y));
        }
      }
      if (g.phase === 'pinch') {
        zoomAround(g.s0, g.tx0, g.ty0, g.c0.x, g.c0.y, g.s0 * (d / Math.max(1, g.d0)), c.x, c.y);
      } else if (g.phase === 'scroll' && canControl()) {
        const dx = c.x - g.lc.x;
        const dy = c.y - g.lc.y;
        input.wheel(-dx * WHEEL_PER_PX, dy * WHEEL_PER_PX);
        g.lc = c;
      }
    }
  }

  function trackpadGain() {
    const mw = (S.state && S.state.mw) || S.frameW || 1920;
    return (mw / Math.max(1, S.layout.w)) * 0.75 * (store.get('sensitivity', 1) || 1);
  }

  function onPointerUp(e) {
    if (e.pointerType === 'mouse' || e.pointerType === 'pen') { onMouseUp(e); return; }
    const p = pointers.get(e.pointerId);
    if (!p) return;
    e.preventDefault();
    const cancelled = e.type === 'pointercancel';
    const before = pointers.size;
    pointers.delete(e.pointerId);
    if (nativeIds.has(e.pointerId)) {
      const id = nativeIds.get(e.pointerId);
      nativeIds.delete(e.pointerId);
      input.touch(id, toRemote(e.clientX, e.clientY), 2);
      return;
    }
    if (!g) return;

    if (g.kind === 'one' && before === 1) {
      clearTimeout(g.timer);
      if (!cancelled && g.phase === 'pending' && canControl()) {
        if (S.mode === 'touch') {
          input.click(0, toRemote(g.x0, g.y0));
          ripple(g.x0, g.y0, false);
        } else if (S.mode === 'trackpad') {
          input.click(0, null);
        }
      }
      if (g.buttonDown) input.button(0, false);
      g = null;
      return;
    }

    if (g.kind === 'two') {
      if (before === 2) {
        const quick = performance.now() - g.t0 < TWO_TAP_MS;
        if (!cancelled && g.phase === 'undecided' && quick && canControl()) {
          const c = { x: (p.x + [...pointers.values()][0].x) / 2, y: (p.y + [...pointers.values()][0].y) / 2 };
          if (S.mode === 'touch') {
            input.click(1, toRemote(c.x, c.y));
            ripple(c.x, c.y, true);
          } else {
            input.click(1, null);
          }
          haptic(10);
        }
        g = { kind: 'done', buttonDown: false, timer: 0 };
      }
    }
    if (pointers.size === 0) g = null;
  }

  // Mouse & pen: direct absolute control with real buttons and wheel.
  const DOM_TO_REMOTE_BUTTON = { 0: 0, 1: 2, 2: 1, 3: 3, 4: 4 };

  function onMouseDown(e) {
    e.preventDefault();
    if (!canControl()) return;
    try { el.stage.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    const b = DOM_TO_REMOTE_BUTTON[e.button] ?? 0;
    mouseDown = { b, id: e.pointerId };
    input.button(b, true, toRemote(e.clientX, e.clientY));
  }

  function onMouseMove(e) {
    if (!canControl()) return;
    input.moveTo(toRemote(e.clientX, e.clientY));
  }

  function onMouseUp(e) {
    e.preventDefault();
    if (!mouseDown) return;
    const b = DOM_TO_REMOTE_BUTTON[e.button] ?? mouseDown.b;
    input.button(b, false, toRemote(e.clientX, e.clientY));
    mouseDown = null;
  }

  function onWheel(e) {
    e.preventDefault();
    if (e.ctrlKey && S.mode === 'view') {
      const s0 = S.view.s;
      zoomAround(s0, S.view.tx, S.view.ty, e.clientX, e.clientY, s0 * Math.exp(-e.deltaY * 0.01), e.clientX, e.clientY);
      return;
    }
    if (!canControl()) return;
    const k = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 400 : 1.2;
    input.moveTo(toRemote(e.clientX, e.clientY));
    input.wheel(e.deltaX * k, -e.deltaY * k);
  }

  el.stage.addEventListener('pointerdown', onPointerDown, { passive: false });
  el.stage.addEventListener('pointermove', onPointerMove, { passive: false });
  el.stage.addEventListener('pointerup', onPointerUp, { passive: false });
  el.stage.addEventListener('pointercancel', onPointerUp, { passive: false });
  el.stage.addEventListener('wheel', onWheel, { passive: false });
  el.stage.addEventListener('contextmenu', (e) => e.preventDefault());
  document.addEventListener('gesturestart', (e) => e.preventDefault());
  document.addEventListener('dblclick', (e) => e.preventDefault());

  // ------------------------------------------------------------------ UI
  let toastTimer = 0;
  function toast(text, ms = 1800) {
    el.toast.textContent = text;
    el.toast.classList.remove('hidden');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.toast.classList.add('hidden'), ms);
  }

  function showOverlay(title, text, opts = {}) {
    el.ovTitle.textContent = title;
    el.ovText.textContent = text || '';
    el.ovSpinner.classList.toggle('hidden', !opts.spinner);
    el.overlay.classList.toggle('passive', !!opts.passive);
    el.pairForm.classList.toggle('hidden', !opts.pair);
    if (opts.action) {
      el.ovAction.textContent = opts.action.label;
      el.ovAction.onclick = opts.action.run;
      el.ovAction.classList.remove('hidden');
    } else {
      el.ovAction.classList.add('hidden');
      el.ovAction.onclick = null;
    }
    el.overlay.classList.remove('hidden');
  }

  function hideOverlay() {
    el.overlay.classList.add('hidden');
  }

  function refreshOverlay() {
    renderButtons();
    if (!S.connected || S.pairing) return;
    const st = S.state || {};
    const disp = st.display || {};
    if (S.paused) {
      showOverlay('Stream paused', 'Resume to see your extra display again.', { action: { label: 'Resume', run: () => setPaused(false) } });
    } else if (st.hostStreaming === false) {
      showOverlay('Paused on the PC', 'Streaming was stopped from the PC. It will resume automatically.', { passive: S.hasFrame });
    } else if (disp.phase === 'creating') {
      showOverlay('Setting up your display…', 'Windows is adding a monitor sized for this device. This takes a few seconds.', { spinner: true, passive: S.hasFrame });
    } else if (st.missing) {
      showOverlay('Display not ready', 'The virtual display is being attached…', { spinner: true, passive: S.hasFrame });
    } else if (st.blocked) {
      showOverlay('Screen unavailable', 'The PC is showing a secure screen (lock screen or admin prompt).', { passive: S.hasFrame });
    } else if (!S.hasFrame) {
      showOverlay('Starting stream…', '', { spinner: true });
    } else {
      hideOverlay();
    }
    renderButtons();
  }

  function renderBanner() {
    const st = S.state || {};
    const disp = st.display || {};
    const drv = st.driver || (S.welcome && S.welcome.driver) || {};
    let key = '';
    let text = '';
    let info = false;
    let action = null;
    if (S.displayMode === 'extend' && disp.phase === 'failed') {
      if (drv.status === 'missing') {
        key = 'driver-missing';
        text = `This PC has no virtual display driver yet, so you are seeing its main screen. On the PC, open ${drv.connect || 'the ScreenExtend connection page'} and click “Install driver”.`;
        action = { label: 'Use mirror mode', run: () => { S.displayMode = 'mirror'; store.set('displayMode', 'mirror'); sendDisplaySettings(); } };
      } else {
        key = `display-failed:${disp.error || ''}`;
        text = `Could not create a virtual display: ${disp.error || 'unknown error'}. Showing the main screen instead.`;
        action = { label: 'Retry', run: () => sendDisplaySettings() };
      }
    } else if (S.displayMode === 'extend' && disp.phase === 'ready' && st.virtual && disp.w) {
      key = `ready:${st.device}:${disp.w}x${disp.h}:${disp.scale}`;
      text = `This device is now display ${(st.monitor || 0) + 1} of the PC: ${disp.w}×${disp.h}${disp.portrait ? ' portrait' : ''}${disp.scale ? `, ${disp.scale}% scaling` : ''}. Drag windows here, or tap Apps.`;
      info = true;
      action = { label: 'Apps', run: openApps };
      if (!S.bannerAutoHide) { S.bannerAutoHide = setTimeout(() => { S.bannerDismissed = key; renderBanner(); S.bannerAutoHide = 0; }, 9000); }
    }
    if (!key || S.bannerDismissed === key) {
      el.banner.classList.add('hidden');
      return;
    }
    el.banner.classList.toggle('info', info);
    el.bannerText.textContent = text;
    el.bannerAction.classList.toggle('hidden', !action);
    if (action) {
      el.bannerAction.textContent = action.label;
      el.bannerAction.onclick = action.run;
    }
    el.banner.dataset.key = key;
    el.banner.classList.remove('hidden');
  }
  el.bannerClose.addEventListener('click', () => {
    S.bannerDismissed = el.banner.dataset.key || '';
    el.banner.classList.add('hidden');
  });

  function renderButtons() {
    el.btnMode.innerHTML = ICONS[S.mode];
    el.btnMode.title = MODE_LABEL[S.mode];
    el.btnPause.innerHTML = S.paused ? ICONS.play : ICONS.pause;
    el.btnPause.setAttribute('aria-label', S.paused ? 'Resume stream' : 'Pause stream');
    el.btnFull.innerHTML = document.fullscreenElement ? ICONS.exitFull : ICONS.full;
    el.btnFull.classList.toggle('hidden', !document.documentElement.requestFullscreen && !document.documentElement.webkitRequestFullscreen);
    el.btnKbd.innerHTML = ICONS.keyboard;
    el.btnKbd.classList.toggle('on', kb.open);
    el.btnKbd.disabled = !canControl();
    el.btnApps.innerHTML = ICONS.apps;
    el.btnApps.disabled = !S.connected || (S.state && S.state.control === false);
    el.btnSettings.innerHTML = ICONS.settings;
    el.appsRefresh.innerHTML = ICONS.refresh;
  }

  function setPaused(p) {
    S.paused = p;
    requestStream();
    refreshOverlay();
  }

  el.btnMode.addEventListener('click', () => {
    const list = modes();
    cancelGesture();
    S.mode = list[(list.indexOf(S.mode) + 1) % list.length];
    store.set('mode', S.mode);
    renderButtons();
    toast(MODE_LABEL[S.mode]);
    reportStats();
  });
  el.btnPause.addEventListener('click', () => setPaused(!S.paused));
  el.btnFull.addEventListener('click', async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else if (document.documentElement.requestFullscreen) await document.documentElement.requestFullscreen({ navigationUI: 'hide' });
      else if (document.documentElement.webkitRequestFullscreen) document.documentElement.webkitRequestFullscreen();
    } catch { toast('Fullscreen is not available here'); }
  });
  document.addEventListener('fullscreenchange', () => { renderButtons(); setTimeout(() => { layout(); reportScreen(); }, 50); });
  el.hudToggle.addEventListener('click', () => {
    el.hud.classList.toggle('collapsed');
    store.set('hudCollapsed', el.hud.classList.contains('collapsed'));
  });
  if (store.get('hudCollapsed', false)) el.hud.classList.add('collapsed');
  el.zoomChip.addEventListener('click', resetView);

  for (const node of [el.hud, el.banner, el.zoomChip, el.overlay, el.keybar, el.sheet, el.apps]) {
    node.addEventListener('pointerdown', (e) => e.stopPropagation());
  }

  window.addEventListener('resize', () => { layout(); reportScreen(); });
  window.addEventListener('orientationchange', () => setTimeout(() => { layout(); reportScreen(); }, 200));
  if (screen.orientation && screen.orientation.addEventListener) {
    screen.orientation.addEventListener('change', () => setTimeout(() => { layout(); reportScreen(); }, 200));
  }
  document.addEventListener('visibilitychange', () => {
    S.hidden = document.visibilityState === 'hidden';
    if (S.hidden) cancelGesture();
    requestStream();
    if (!S.hidden) acquireWakeLock();
  });

  // ------------------------------------------------------------------ pairing
  const urlParams = new URLSearchParams(location.search);
  let autoCode = (urlParams.get('code') || '').replace(/\D/g, '');
  if (urlParams.has('code')) {
    try { history.replaceState(null, '', location.pathname); } catch { /* ignore */ }
  }

  function showPairing(host) {
    S.pairing = true;
    closeKeyboard();
    closeSettings();
    closeApps();
    showOverlay(host ? `Pair with ${host}` : 'Pair with your PC',
      'Enter the 6-digit pairing code shown by ScreenExtend on the PC (tray icon menu or connection page).', { pair: true });
    el.pairMsg.textContent = '';
    el.pairMsg.classList.remove('error');
    if (autoCode.length === 6) {
      el.pairCode.value = autoCode;
      autoCode = '';
      pair();
    } else {
      setTimeout(() => { try { el.pairCode.focus(); } catch { /* ignore */ } }, 50);
    }
  }

  async function pair() {
    const code = el.pairCode.value.replace(/\D/g, '');
    if (code.length !== 6) {
      el.pairMsg.textContent = 'The code has 6 digits.';
      el.pairMsg.classList.add('error');
      return;
    }
    if (el.pairBtn.disabled) return;
    el.pairBtn.disabled = true;
    el.pairMsg.classList.remove('error');
    el.pairMsg.textContent = 'Pairing…';
    try {
      const res = await fetch('/api/pair', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code, name: deviceName() }),
      });
      let body = {};
      try { body = await res.json(); } catch { /* not JSON */ }
      if (!res.ok || !body.token) throw new Error(body.error || `Pairing failed (${res.status})`);
      store.set('token', body.token);
      el.pairCode.value = '';
      el.pairMsg.textContent = '';
      S.pairing = false;
      showOverlay('Paired', 'Setting up your display…', { spinner: true });
      if (S.ws && S.ws.readyState === 1) sendHello(); else connect();
    } catch (e) {
      el.pairMsg.textContent = (e && e.message) || 'Pairing failed';
      el.pairMsg.classList.add('error');
      el.pairCode.select();
    } finally {
      el.pairBtn.disabled = false;
    }
  }

  el.pairForm.addEventListener('submit', (e) => { e.preventDefault(); pair(); });
  el.pairCode.addEventListener('input', () => {
    const d = el.pairCode.value.replace(/\D/g, '').slice(0, 6);
    el.pairCode.value = d.length > 3 ? `${d.slice(0, 3)} ${d.slice(3)}` : d;
    if (d.length === 6) pair();
  });

  // ------------------------------------------------------------------ keyboard
  const SENTINEL = '​​';
  const kb = { open: false, prev: SENTINEL, composing: false, mods: new Set() };
  const PASSTHROUGH_KEYS = new Set(['Escape', 'Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete', 'Home', 'End',
    'PageUp', 'PageDown', 'Insert', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12', 'ContextMenu']);
  const MODIFIER_KEYS = new Set(['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'CapsLock', 'NumLock', 'OS']);

  function sendKey(k, mods) {
    if (!canControl()) return;
    const m = [...new Set([...(mods || []), ...kb.mods])];
    input.now(m.length ? { t: 'kk', k, m } : { t: 'kk', k });
    clearStickyMods();
  }

  function sendText(text) {
    if (!text || !canControl()) return;
    if (kb.mods.size) {
      sendKey(text[0]);
      return;
    }
    input.now({ t: 'kt', s: text });
  }

  function clearStickyMods() {
    if (!kb.mods.size) return;
    kb.mods.clear();
    for (const b of el.keybar.querySelectorAll('[data-mod]')) b.classList.remove('on');
  }

  function resetSink() {
    el.kbd.value = SENTINEL;
    kb.prev = SENTINEL;
    try { el.kbd.setSelectionRange(SENTINEL.length, SENTINEL.length); } catch { /* ignore */ }
  }

  function syncSink() {
    const v = el.kbd.value;
    const old = kb.prev;
    let i = 0;
    while (i < old.length && i < v.length && old[i] === v[i]) i++;
    const removed = old.length - i;
    const added = v.slice(i).replace(/​/g, '');
    for (let n = 0; n < removed; n++) sendKey('Backspace');
    if (added) sendText(added);
    kb.prev = v;
    if (!kb.composing && (!v.startsWith(SENTINEL) || v.length > 64 || v.includes('\n'))) resetSink();
  }

  function openKeyboard() {
    if (!canControl()) { toast('Remote control is not available'); return; }
    kb.open = true;
    resetSink();
    el.kbd.focus({ preventScroll: true });
    el.keybar.classList.remove('hidden');
    placeKeybar();
    renderButtons();
  }

  function closeKeyboard() {
    if (!kb.open) return;
    kb.open = false;
    clearStickyMods();
    el.kbd.blur();
    el.keybar.classList.add('hidden');
    renderButtons();
  }

  function placeKeybar() {
    const vv = window.visualViewport;
    const offset = vv ? Math.max(0, window.innerHeight - (vv.height + vv.offsetTop)) : 0;
    el.keybar.style.bottom = `${offset}px`;
  }
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', placeKeybar);
    window.visualViewport.addEventListener('scroll', placeKeybar);
  }

  function modsOf(e) {
    const m = [];
    if (e.ctrlKey) m.push('ctrl');
    if (e.altKey) m.push('alt');
    if (e.shiftKey) m.push('shift');
    if (e.metaKey) m.push('win');
    return m;
  }

  el.kbd.addEventListener('compositionstart', () => { kb.composing = true; });
  el.kbd.addEventListener('compositionend', () => { kb.composing = false; syncSink(); });
  el.kbd.addEventListener('input', syncSink);
  el.kbd.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;
    const combo = e.ctrlKey || e.metaKey || (e.altKey && !e.ctrlKey);
    if (PASSTHROUGH_KEYS.has(e.key) || (combo && e.key.length === 1)) {
      e.preventDefault();
      sendKey(e.key, modsOf(e));
    }
  });
  el.kbd.addEventListener('blur', () => {
    setTimeout(() => { if (kb.open && document.activeElement !== el.kbd) closeKeyboard(); }, 150);
  });

  el.keybar.addEventListener('pointerdown', (e) => { if (e.target.closest('button')) e.preventDefault(); });
  el.keybar.addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.act === 'close') { closeKeyboard(); return; }
    if (b.dataset.mod) {
      const m = b.dataset.mod;
      if (kb.mods.has(m)) kb.mods.delete(m); else kb.mods.add(m);
      b.classList.toggle('on', kb.mods.has(m));
    } else if (b.dataset.key) {
      sendKey(b.dataset.key);
    }
    haptic(8);
    if (kb.open) el.kbd.focus({ preventScroll: true });
  });

  document.addEventListener('keydown', (e) => {
    const tag = e.target && e.target.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA' || S.pairing || !canControl()) return;
    if (!el.sheet.classList.contains('hidden') || !el.apps.classList.contains('hidden')) return;
    if (MODIFIER_KEYS.has(e.key) || e.key === 'Unidentified' || e.key === 'Dead' || e.isComposing) return;
    e.preventDefault();
    const altGr = e.ctrlKey && e.altKey;
    if (e.key.length === 1 && !e.metaKey && (altGr || (!e.ctrlKey && !e.altKey))) sendText(e.key);
    else sendKey(e.key, modsOf(e));
  });

  el.btnKbd.addEventListener('click', () => (kb.open ? closeKeyboard() : openKeyboard()));

  // ------------------------------------------------------------------ apps (windows on this display)
  function openApps() {
    if (!S.connected) return;
    closeKeyboard();
    closeSettings();
    el.appsList.innerHTML = '<li class="empty">Loading…</li>';
    el.apps.classList.remove('hidden');
    send({ t: 'windows' });
  }

  function closeApps() {
    el.apps.classList.add('hidden');
  }

  function renderApps(m) {
    if (el.apps.classList.contains('hidden')) return;
    const list = m.list || [];
    el.appsList.textContent = '';
    if (!list.length) {
      const li = document.createElement('li');
      li.className = 'empty';
      li.textContent = 'No application windows are open on the PC.';
      el.appsList.appendChild(li);
      return;
    }
    list.sort((a, b) => Number(b.here) - Number(a.here) || a.title.localeCompare(b.title));
    for (const w of list) {
      const li = document.createElement('li');
      li.className = w.here ? 'here' : '';
      const dot = document.createElement('span');
      dot.className = 'app-dot';
      const text = document.createElement('div');
      text.className = 'app-text';
      const title = document.createElement('div');
      title.className = 'app-title';
      title.textContent = w.title;
      const sub = document.createElement('div');
      sub.className = 'app-sub';
      sub.textContent = [w.proc, w.here ? 'on this display' : '', w.min ? 'minimized' : ''].filter(Boolean).join(' · ');
      text.append(title, sub);
      const act = document.createElement('span');
      act.className = 'app-act';
      act.textContent = w.here ? 'Focus' : 'Move here';
      li.append(dot, text, act);
      li.addEventListener('click', () => {
        send(w.here ? { t: 'focuswin', h: w.h } : { t: 'movewin', h: w.h, max: true });
        haptic(8);
        toast(w.here ? `Focusing ${w.title}` : `Moving ${w.title} here…`);
      });
      el.appsList.appendChild(li);
    }
  }

  el.btnApps.addEventListener('click', openApps);
  el.appsClose.addEventListener('click', closeApps);
  el.appsRefresh.addEventListener('click', () => send({ t: 'windows' }));
  el.appsMoveFg.addEventListener('click', () => { send({ t: 'movefg', max: true }); haptic(8); });
  el.apps.addEventListener('click', (e) => { if (e.target === el.apps) closeApps(); });

  // ------------------------------------------------------------------ settings
  function renderSettings() {
    const mons = (S.welcome && S.welcome.monitors) || [];
    const sel = el.setMode;
    const physical = mons.filter((x) => !x.virtual);
    const sig = `${S.displayMode}|${physical.map((x) => `${x.i}:${x.label}`).join('|')}`;
    if (sel.dataset.sig !== sig) {
      sel.dataset.sig = sig;
      sel.textContent = '';
      const o = document.createElement('option');
      o.value = 'extend';
      o.textContent = 'Extra display (extend)';
      sel.appendChild(o);
      for (const x of physical) {
        const m = document.createElement('option');
        m.value = `mirror:${x.i}`;
        m.textContent = `Mirror ${x.label}`;
        sel.appendChild(m);
      }
    }
    sel.value = S.displayMode === 'extend' ? 'extend' : `mirror:${S.monitor}`;
    if (sel.value !== (S.displayMode === 'extend' ? 'extend' : `mirror:${S.monitor}`)) sel.value = 'extend';
    for (const f of document.querySelectorAll('.extend-only')) f.classList.toggle('hidden', S.displayMode !== 'extend');
    el.setRes.value = String(S.res);
    el.setScale.value = String(S.scale);
    el.setQuality.value = S.quality;
    el.setSens.value = String(store.get('sensitivity', 1) || 1);
    if (document.activeElement !== el.setName) el.setName.value = deviceName();
    const host = S.welcome && S.welcome.host;
    const disp = (S.state && S.state.display) || {};
    let info = host ? `Connected to ${host} (${location.host}).` : `Server: ${location.host}`;
    if (S.displayMode === 'extend' && disp.phase === 'ready' && disp.w) info += ` Virtual display: ${disp.w}×${disp.h}${disp.portrait ? ' portrait' : ''}${disp.scale ? `, ${disp.scale}%` : ''}.`;
    el.setInfo.textContent = info;
  }

  function openSettings() {
    closeKeyboard();
    closeApps();
    renderSettings();
    el.sheet.classList.remove('hidden');
  }

  function closeSettings() {
    el.sheet.classList.add('hidden');
  }

  el.btnSettings.addEventListener('click', openSettings);
  el.setClose.addEventListener('click', closeSettings);
  el.sheet.addEventListener('click', (e) => { if (e.target === el.sheet) closeSettings(); });
  el.setMode.addEventListener('change', () => {
    const v = el.setMode.value;
    if (v === 'extend') S.displayMode = 'extend';
    else {
      S.displayMode = 'mirror';
      S.monitor = Number(v.split(':')[1]) || 0;
    }
    store.set('displayMode', S.displayMode);
    store.set('monitor', S.monitor);
    S.bannerDismissed = '';
    resetView();
    renderSettings();
    sendDisplaySettings();
  });
  el.setRes.addEventListener('change', () => {
    S.res = Number(el.setRes.value) || 0;
    store.set('res', S.res);
    S.bannerDismissed = '';
    sendDisplaySettings();
  });
  el.setScale.addEventListener('change', () => {
    S.scale = Number(el.setScale.value) || 0;
    store.set('scale', S.scale);
    S.bannerDismissed = '';
    sendDisplaySettings();
  });
  el.setQuality.addEventListener('change', () => {
    S.quality = el.setQuality.value;
    store.set('quality', S.quality);
    requestStream();
  });
  el.setSens.addEventListener('input', () => store.set('sensitivity', Number(el.setSens.value) || 1));
  el.setName.addEventListener('change', () => {
    store.set('name', el.setName.value.trim().slice(0, 40));
    if (S.paired) sendHello();
  });
  el.setForget.addEventListener('click', () => {
    store.set('token', '');
    closeSettings();
    S.stopped = true;
    clearTimeout(reconnectTimer);
    send({ t: 'forget' });
    if (S.ws) S.ws.close();
    showOverlay('This PC was forgotten', 'This device is no longer paired with the PC.', { action: { label: 'Pair again', run: () => connect() } });
  });

  // Keep the phone screen awake while it is a display (only available in secure contexts).
  let wakeLock = null;
  async function acquireWakeLock() {
    if (!('wakeLock' in navigator) || wakeLock) return;
    try {
      wakeLock = await navigator.wakeLock.request('screen');
      wakeLock.addEventListener('release', () => { wakeLock = null; });
    } catch { /* not allowed */ }
  }

  // ------------------------------------------------------------------ boot
  renderButtons();
  layout();
  connect();
  acquireWakeLock();
  setInterval(updateStats, 500);
  setInterval(reportStats, 2000);

  // Test hook (used by the automated end-to-end test).
  window.__se = { state: S, stats, clock, kb, send };
})();
