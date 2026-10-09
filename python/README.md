# screenextend

Turn a phone or tablet into an **extra monitor** for a Windows PC. The device only needs a web browser.
When it connects, the PC gets a new virtual display sized and rotated to match the device's screen. Drag any
window onto it, or pull a window over from the phone itself, and work with it by touch, trackpad gestures,
native multi-touch or the keyboard.

```
pip install screenextend
screenextend
```

That is all: the host starts in the notification area and opens its connection page. Scan the QR code
with the phone (same Wi-Fi), confirm the 6-digit code, and after a few seconds the phone is a new display.

The first time, click **Install driver** on the connection page (or run `screenextend --install-driver`).
This fetches the free, signed Parsec Virtual Display Driver and installs it (Windows asks for administrator
permission). Without it the phone can only mirror an existing screen.

## Requirements

- Windows 10 (21H2+) or Windows 11, x64.
- Python 3.8+ (only for the launcher).
- .NET 10 runtime (Desktop + ASP.NET Core). If it is missing, `screenextend` offers to install it for the
  current user without administrator rights (`screenextend --install-runtime` does it explicitly;
  `--no-install-runtime` prevents it).

## Command

```
screenextend                 start in the tray (default), open the connection page
screenextend --no-tray       run in this console, Ctrl+C to stop
screenextend --port 9000     another port
screenextend --install-driver
screenextend --driver-test   diagnostic for the virtual display driver
screenextend --doctor        show exe and runtime status
screenextend --help
```

All other options are passed through to the host (`--no-browser`, `--data-dir`, `--webroot`, `--verbose`).
`python -m screenextend` works too.

## What you get

- One virtual display per connected device, matched to its aspect ratio, resolution preset and orientation
  (rotating the phone rotates the display), with Windows scaling set so UI is phone sized.
- Partial frame streaming: only changed regions are sent, so a mostly static screen costs little bandwidth.
- Touch, trackpad, native multi-touch (real Windows touch contacts: pinch, swipe, ink), keyboard with modifier bar.
- Apps sheet on the phone: list the PC's windows, move any onto the phone's display, or grab the active window.
- Pairing with one-time codes, per-device control permission, localhost-only admin page, DNS-rebinding guard.

Full documentation, source code and the C# host live in the project repository.

## Security

For a trusted home/office LAN only (plain HTTP, no TLS). A paired device with control can do anything you
can with mouse, keyboard and touch. Revoke devices from the tray menu.
