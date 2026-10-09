# ScreenExtend

Turn a phone or tablet into an **extra monitor** for a Windows PC. The device only needs a web browser;
nothing is installed on it. When it connects, the PC gets a new virtual display sized and rotated to match
the device's screen. Drag any window onto it, or pull a window over from the phone itself, and work with it
by touch, trackpad gestures, native multi-touch or the keyboard.

ScreenExtend is a sibling of [ScreenMirror](../ScreenMirror) (which mirrors an existing screen). It shares
the pairing, streaming and security model, and adds virtual displays, partial frame updates, touch
injection and window management.

## How it works

| Piece | What it does |
|---|---|
| **Virtual display** | The host drives the free, signed [Parsec Virtual Display Driver](https://support.parsec.app/hc/en-us/sections/32361161093780-Virtual-Display-Driver-VDD) (an Indirect Display Driver) through the control protocol documented by the MIT-licensed [parsec-vdd](https://github.com/nomi-san/parsec-vdd) project: one virtual monitor per connected device, plugged in on connect and unplugged ~25 s after disconnect so a reconnecting phone finds its windows where they were. The driver is kept alive by pings; if ScreenExtend crashes, the driver removes the displays on its own. |
| **Fit to the device** | The host picks the display mode that best matches the device's aspect ratio (a 20:9 phone gets a 16:9 or 21:9 mode, a 16:10 tablet gets 1920x1200, ...), rotates the display when the phone is held in portrait (and again when it is turned), and sets Windows scaling so UI elements come out phone sized. Resolution cap and scaling can be overridden per device. |
| **Streaming** | JPEG over WebSocket with ack-based flow control, as in ScreenMirror, plus **partial frames**: when only a region changes (cursor, caret, a scrolling pane) just that rectangle is encoded and sent. Full frames are sent on demand, on resize, and as a sharper "refine" frame once the screen goes still. |
| **Input** | Touch mode (tap/long-press/drag/two-finger scroll), trackpad mode, **native touch** mode (fingers become real Windows touch contacts via `InjectTouchInput`: pinch-zoom, swipe, inking in apps), keyboard with modifier bar, mouse/pen for desktop browsers. Input is aimed at the device's own display. |
| **Apps** | The phone lists the PC's application windows and can move any of them onto its display (maximized) or focus one already there; "Move the active PC window here" grabs whatever you were just using. |
| **Fallback** | Without the driver, devices mirror the main display (as ScreenMirror does) and the phone explains how to install the driver from the PC. |

Host: C# / .NET 10, Windows 10/11 only, single small exe.

## Quick start

### As a Python package (easiest for users)

```
pip install screenextend
screenextend
```

The `python/` folder holds a pip package that bundles the built host exe and a `screenextend` command. It
checks for the .NET 10 runtime and installs it per user (no admin) when missing, then starts the host in the
tray. `screenextend --no-tray`, `--install-driver`, `--driver-test`, `--doctor`, `--help`. See `python/README.md`.

To build and publish the package yourself:

```bat
build-python.bat                       -> python\dist\screenextend-<ver>-py3-none-win_amd64.whl + .tar.gz
python -m pip install twine
python -m twine upload python\dist\*   -> PyPI (needs an account and API token; use --repository testpypi first)
```

Bump the version in both `python/pyproject.toml` and `python/screenextend/__init__.py` before each upload.

### From source

Requires the .NET 10 SDK to build and the .NET 10 Desktop + ASP.NET Core runtimes to run.

```bat
build.bat
dist\ScreenExtend.exe
```

For development: `dotnet run --project src\ScreenExtend -- --no-tray`. Tests: `dotnet test`.

The exe is a windowed app: it puts an icon in the notification area and opens the connection page
(`http://localhost:8700/connect`) in the browser. The page shows the driver status, the phone address, a QR
code and the 6-digit pairing code.

### Install the virtual display driver (once)

Click **Install driver** on the connection page (or the tray menu, or run `ScreenExtend.exe --install-driver`).
ScreenExtend downloads `parsec-vdd-0.45.0.0.exe` from `https://builds.parsec.app/vdd/` into its data folder and
runs it silently; Windows asks for administrator permission and shows the publisher (Parsec Cloud, Inc.).
The driver is Parsec's and is used under Parsec's terms; ScreenExtend only talks to it. You can also
install it yourself from Parsec's site; ScreenExtend detects it on the next connection.

Without the driver everything still works in **mirror** mode.

### Connect a device

1. Put the phone on the same Wi-Fi as the PC.
2. Scan the QR code (it carries a one-time pairing link) or open the address and type the code.
3. After a few seconds the phone becomes a new display of the PC. A banner shows its resolution and
   scaling. Drag windows to it, or tap **Apps** on the phone and pick a window.

Paired devices store a token and reconnect automatically. **Forget this PC** on the device (Settings)
removes the pairing on both sides; the tray menu can revoke devices too.

## On the device

Toolbar (top): connection quality · fps · latency, then:

- **Input mode** (cycles): Touch → Touchscreen (native, when available) → Trackpad → View only.
  - Touch: tap = click, long-press = right-click, drag = click-drag, two-finger swipe = scroll, two-finger
    tap = right-click, pinch = zoom the view.
  - Touchscreen: every finger is a real touch contact on the virtual display (apps see pinch, swipe, pan, ink).
  - Trackpad: swipe moves the pointer, tap = click, two-finger tap = right-click, hold then move = drag.
  - View only: no input; pinch/drag zooms and pans the view.
- **Apps**: windows on the PC; tap to move one here (maximized) or focus it; button to move the active window.
- **Keyboard**: on-screen keyboard plus Ctrl/Alt/Shift/Win, Esc, Tab, arrows, Del, Home/End, PgUp/PgDn, F5, Enter.
- **Pause**, **Fullscreen**, **Settings**.

Settings: *Use this device as* (extra display, or mirror a physical display), *Resolution* (auto ≈ up to
1080p, lighter 720p, up to 900p/1440p/4K, largest), *Windows scaling* (auto or 100–250 %), *Stream quality*
(Native/High/Balanced/Low: cap on the streamed long side and JPEG quality), trackpad speed, device name.

Rotating the phone rotates the virtual display (landscape ↔ portrait) after a short delay.

## On the PC

- **Connection page** `http://localhost:<port>/connect` (PC only): driver status + install button, pairing
  code/QR, virtual displays (owner, mode, scaling), devices (status, display, speed, control), remove all displays.
- **Tray menu**: open connection page, pairing code/link, new code, driver status / install, remove all
  virtual displays, pause streaming, allow remote control (global and per device), remove/revoke devices,
  open data folder, quit.

Data lives in `%APPDATA%\ScreenExtend`: `devices.json` (SHA-256 token hashes, per-device control flag),
`screenextend.log`, `port.txt`, `driver\` (downloaded installer). Delete the folder to reset pairing.

## Command-line options

| Option | Default | Description |
|---|---|---|
| `--port N`, `-p N` | 8700 | Port to listen on. If omitted and busy, 8701-8709 are tried |
| `--no-tray`, `--headless` | off | Run attached to the parent console; Ctrl+C to quit |
| `--no-browser` | off | Don't open the connection page at startup |
| `--install-driver` | | Download and install the virtual display driver (UAC prompt), then exit |
| `--driver-test` | | Diagnostic: plug one virtual display, print what Windows reports for 12 s, unplug it |
| `--data-dir PATH` | `%APPDATA%\ScreenExtend` | Data folder; one instance runs per data directory |
| `--webroot PATH` | embedded | Serve web files from a folder instead of the embedded copy (UI development) |
| `--verbose`, `-v` | off | Verbose logging |

## Protocol notes (for hacking)

WebSocket at `/ws`, JSON text messages plus binary frames. Frame header (36 bytes, little endian):
type u8 (1) · flags u8 (bit0 refined, bit1 cursor visible, bit2 partial) · source u16 · frame id u32 ·
capture time f64 (Unix ms) · frame w/h u16 · cursor x/y f32 (normalized) · region x/y/w/h u16 · JPEG.
A partial frame's JPEG covers only the region; the client requests a keyframe (`{"t":"key"}`) if it has
no full frame of that size yet.

Client → host: `hello` (token, name, mode extend|mirror, monitor, quality, res, scale, screen{sw,sh,vw,vh,dpr,portrait}),
`screen` (rotation/resize), `display` (mode/monitor/res/scale), `stream`, `key`, `ack`, `ping`, `stats`,
`windows`, `movewin {h,max}`, `movefg {max}`, `focuswin {h}`, input `mm mr mb mc wh kt kk kd ku` and
`tc {p:[{i,x,y,s}]}` for native touch (s: 0 down, 1 move, 2 up), `forget`.
Host → client: `welcome` (monitors, driver, touch, resolutions, scales), `state` (incl. `display{phase,w,h,scale,portrait,error}`),
`windows`, `toast`, `pong`, `unpaired`, `bye`.

## Windows Firewall

Allow the app on **Private** networks when Windows asks, or add a rule (elevated prompt):

```bat
netsh advfirewall firewall add rule name="ScreenExtend" dir=in action=allow protocol=TCP localport=8700 profile=private
```

## Security notes

- For a trusted home/office LAN only: plain HTTP/WebSocket, no TLS. Do not port-forward it.
- A paired device with control can do anything you can with mouse, keyboard and touch on the PC.
- Devices must pair with the one-time 6-digit code (replaced on use and after 5 failures, with a 30 s pause).
  The connection page, QR endpoint, driver install and display removal are localhost-only. Requests whose
  Host header is not this PC get 421 (DNS rebinding guard); WebSocket/pairing requests from another web
  origin get 403.
- The driver installer is downloaded over HTTPS from Parsec's build server and run through the normal UAC
  prompt, which shows the signer. ScreenExtend checks only that the file is a Windows executable of a
  plausible size; look at the UAC dialog before approving.

## Troubleshooting

- **Phone shows the main screen instead of its own display**: the driver is not installed (banner on the
  phone, "not installed" on the connection page). Install it on the PC; the phone reconnects on its own.
- **Driver installs but no display appears**: Windows sometimes connects the new monitor as a duplicate of the
  main screen; ScreenExtend asks for the "extend" topology automatically, but if the phone still mirrors, open
  Windows Display settings and set the ParsecVDA monitor to *Extend*. `Error` status on the connection page:
  restart Windows once; check `screenextend.log`. Another program using the Parsec driver (Parsec itself)
  can also hold it. `ScreenExtend.exe --driver-test` plugs one display and prints what Windows reports.
- **Scaling lower than requested**: Windows caps the scaling choices per resolution (a 1080p display allows
  up to 175 %). Pick a higher *Resolution* to get larger scaling steps.
- **Display is created but text is tiny/huge**: change *Windows scaling* in the phone's Settings; *Auto*
  targets phone-sized UI.
- **Window won't move**: elevated (administrator) windows cannot be moved or controlled from a non-elevated
  ScreenExtend; run it as administrator if you need that.
- **Touchscreen mode does nothing**: touch injection needs Windows 8+ and may be unavailable in some
  remote sessions; the mode is hidden when the host reports it unavailable.
- **Laggy**: lower *Stream quality* or *Resolution* on the phone, prefer 5 GHz Wi-Fi.
- **Phone can't load the page**: same network (not guest Wi-Fi), firewall rule above, try every address
  the connection page lists; VPN software can block LAN access.
- **"Could not listen on port"**: pass `--port`, or close the other program.
- Logs: `%APPDATA%\ScreenExtend\screenextend.log` (`--verbose` for more).
