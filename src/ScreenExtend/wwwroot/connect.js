/* ScreenExtend connection page (shown on the PC): link, QR code, pairing code, driver status, displays and devices. */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  let urlIndex = 0;
  let qrKey = '';
  let installing = false;

  function cell(text, cls) {
    const td = document.createElement('td');
    if (cls) {
      const dot = document.createElement('span');
      dot.className = cls;
      td.appendChild(dot);
    }
    td.appendChild(document.createTextNode(text));
    return td;
  }

  function emptyRow(body, text, cols) {
    const tr = document.createElement('tr');
    const td = cell(text);
    td.colSpan = cols;
    td.className = 'empty';
    tr.appendChild(td);
    body.appendChild(tr);
  }

  function renderDriver(d) {
    const btn = $('driverBtn');
    const inst = (d && d.install) || { state: 'idle', message: '' };
    installing = inst.state === 'downloading' || inst.state === 'installing';
    if (!d) {
      $('driverText').textContent = 'Unknown';
      btn.hidden = true;
      return;
    }
    if (d.status === 'ok') {
      $('driverTitle').textContent = `Virtual display driver: ready${d.version ? ` (${d.name} ${d.version})` : ''}`;
      $('driverText').textContent = `Each connected device gets its own virtual monitor, sized to its screen.${d.touch ? ' Native touch is available.' : ''}`;
      btn.hidden = true;
      return;
    }
    $('driverTitle').textContent = d.status === 'error' ? 'Virtual display driver: error' : 'Virtual display driver: not installed';
    let text = d.detail || '';
    if (d.status !== 'error') {
      text = `Without the driver, devices can only mirror an existing monitor. Installing the free ${d.name} (signed by Parsec Cloud, Inc.) adds real extra displays. Windows will ask for administrator permission.`;
    }
    if (inst.message && inst.state !== 'idle') text += `\n${inst.message}`;
    $('driverText').textContent = text;
    btn.hidden = false;
    btn.disabled = installing;
    btn.textContent = inst.state === 'downloading' ? 'Downloading…' : inst.state === 'installing' ? 'Installing… (approve the prompt)' : inst.state === 'failed' ? 'Try again' : 'Install driver';
  }

  function render(info) {
    $('host').textContent = `${info.host} · port ${info.port}`;
    const code = info.code || '';
    $('code').textContent = code ? `${code.slice(0, 3)} ${code.slice(3)}` : '------';
    if (urlIndex >= info.urls.length) urlIndex = 0;
    $('url').textContent = info.urls[urlIndex];

    const list = $('urls');
    list.textContent = '';
    info.urls.forEach((u, i) => {
      if (i === urlIndex) return;
      const li = document.createElement('li');
      const b = document.createElement('button');
      b.textContent = u;
      b.title = 'Use this address for the QR code';
      b.onclick = () => { urlIndex = i; refresh(); };
      li.append('Also: ', b);
      list.appendChild(li);
    });

    const key = `${urlIndex}|${code}|${info.urls[urlIndex]}`;
    if (key !== qrKey) {
      qrKey = key;
      $('qr').src = `qr.svg?i=${urlIndex}&c=${encodeURIComponent(code)}`;
    }

    renderDriver(info.driver);

    const dbody = $('displays');
    dbody.textContent = '';
    const displays = info.displays || [];
    if (!displays.length) emptyRow(dbody, 'No virtual displays right now. One is created when a device connects.', 4);
    for (const d of displays) {
      const tr = document.createElement('tr');
      tr.appendChild(cell(d.device.replace(/^\\\\\.\\/, ''), d.inUse ? 'dot on' : 'dot warn'));
      tr.appendChild(cell(d.inUse ? d.owner : `${d.owner || '-'} (disconnected, kept for a moment)`));
      tr.appendChild(cell(d.w ? `${d.w}x${d.h}${d.portrait ? ' portrait' : ''}` : 'default'));
      tr.appendChild(cell(d.scale ? `${d.scale}%` : '-'));
      dbody.appendChild(tr);
    }
    $('removeAll').hidden = !displays.length;

    $('status').innerHTML = [
      `Streaming: <b>${info.hostStreaming ? 'on' : 'paused'}</b>`,
      `Remote control: <b>${info.inputEnabled ? 'allowed' : 'blocked'}</b>`,
    ].join('');

    const body = $('devices');
    body.textContent = '';
    const devices = info.devices || [];
    if (!devices.length) emptyRow(body, 'No paired devices yet.', 5);
    for (const d of devices) {
      const tr = document.createElement('tr');
      tr.appendChild(cell(d.name));
      tr.appendChild(cell(d.connected ? `connected (${d.address})` : `last seen ${new Date(d.lastSeen).toLocaleString()}`, d.connected ? 'dot on' : 'dot'));
      let disp = '-';
      if (d.connected) {
        const dev = (d.display || '').replace(/^\\\\\.\\/, '');
        disp = d.mode === 'extend' ? (d.phase === 'ready' ? `own display (${dev})` : d.phase === 'creating' ? 'creating display…' : d.phase === 'failed' ? `mirroring ${dev} (no driver)` : dev) : `mirroring ${dev}`;
      }
      tr.appendChild(cell(disp));
      tr.appendChild(cell(d.connected && d.streaming ? `${d.fps} fps · ${d.latency} ms · ${d.kbps} kbps` : '-'));
      tr.appendChild(cell(d.control ? 'allowed' : 'view only'));
      body.appendChild(tr);
    }
  }

  async function refresh() {
    try {
      const res = await fetch('api/info', { cache: 'no-store' });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      render(await res.json());
    } catch (e) {
      $('status').innerHTML = '<span class="err">ScreenExtend is not running.</span>';
    }
  }

  $('driverBtn').addEventListener('click', async () => {
    if (installing) return;
    $('driverBtn').disabled = true;
    try {
      const res = await fetch('api/driver/install', { method: 'POST' });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.error || `HTTP ${res.status}`);
    } catch (e) {
      $('driverText').textContent = `Could not start the installation: ${e.message}`;
    }
    setTimeout(refresh, 300);
  });

  $('removeAll').addEventListener('click', async () => {
    if (!confirm('Remove all virtual displays? Connected devices will reconnect and get fresh displays.')) return;
    try { await fetch('api/displays/remove-all', { method: 'POST' }); } catch { /* shown on next refresh */ }
    setTimeout(refresh, 500);
  });

  refresh();
  setInterval(refresh, installing ? 1000 : 2000);
})();
