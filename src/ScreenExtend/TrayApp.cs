using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ScreenExtend;

/// <summary>Notification-area icon and menu: the PC-side control surface when running normally (not --no-tray).</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppServer _server;
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly Control _ui; // marshals server events onto the UI thread
    private readonly ToolStripMenuItem _miHeader, _miCode, _miDriver, _miInstall, _miDisplays, _miPause, _miControl, _miDevices, _miRevokeAll;
    private string[] _connected = Array.Empty<string>();
    private InstallState _lastInstallState = InstallState.Idle;

    public TrayApp(AppServer server)
    {
        _server = server;
        _ui = new Control();
        _ui.CreateControl();
        _appIcon = CreateIcon();

        var menu = new ContextMenuStrip();
        _miHeader = new ToolStripMenuItem($"ScreenExtend on {Environment.MachineName}") { Enabled = false };
        _miCode = new ToolStripMenuItem("Pairing code", null, (_, _) => ShowPairingInfo());
        _miDriver = new ToolStripMenuItem("Virtual display driver") { Enabled = false };
        _miInstall = new ToolStripMenuItem("Install virtual display driver…", null, (_, _) => InstallDriver());
        _miDisplays = new ToolStripMenuItem("Remove all virtual displays", null, (_, _) => _server.RemoveAllDisplays());
        _miPause = new ToolStripMenuItem("Pause streaming", null, (_, _) => _server.SetHostStreaming(!_server.Hub.HostStreaming));
        _miControl = new ToolStripMenuItem("Allow remote control", null, (_, _) => _server.InputEnabled = !_server.InputEnabled);
        _miDevices = new ToolStripMenuItem("Paired devices");
        _miDevices.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        _miRevokeAll = new ToolStripMenuItem("Revoke all devices…", null, (_, _) => RevokeAll());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _miHeader,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open in browser", null, (_, _) => OpenBrowser(_server.ConnectPageUrl)) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Show pairing code / link…", null, (_, _) => ShowPairingInfo()),
            _miCode,
            new ToolStripMenuItem("New pairing code", null, (_, _) => _server.Pairing.NewPairingCode()),
            new ToolStripSeparator(),
            _miDriver,
            _miInstall,
            _miDisplays,
            new ToolStripSeparator(),
            _miPause,
            _miControl,
            new ToolStripSeparator(),
            _miDevices,
            _miRevokeAll,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenBrowser(_server.Options.DataDir)),
            new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()),
        });
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenBrowser(_server.ConnectPageUrl);
        _icon.BalloonTipClicked += (_, _) => OpenBrowser(_server.ConnectPageUrl);

        _server.SessionsChanged += () => Post(OnSessionsChanged);
        _server.Pairing.Changed += () => Post(RefreshMenu);
        RefreshMenu();
    }

    public static void OpenBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    private void Post(Action a)
    {
        try
        {
            if (!_ui.IsDisposed) _ui.BeginInvoke(a);
        }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void OnSessionsChanged()
    {
        var now = _server.Sessions.Values.Where(s => s.Authorized).Select(s => s.Name).OrderBy(n => n).ToArray();
        var joined = now.Except(_connected).ToArray();
        _connected = now;
        if (joined.Length > 0)
            _icon.ShowBalloonTip(3000, "Device connected", $"{string.Join(", ", joined)} is now a display of this PC.", ToolTipIcon.Info);
        var st = _server.Installer.State;
        if (st != _lastInstallState && (st == InstallState.Done || st == InstallState.Failed))
            _icon.ShowBalloonTip(5000, "Virtual display driver", _server.Installer.Message, st == InstallState.Done ? ToolTipIcon.Info : ToolTipIcon.Warning);
        _lastInstallState = st;
        RefreshMenu();
    }

    internal void RefreshMenu()
    {
        string code = _server.Pairing.Code;
        int connected = _server.Sessions.Values.Count(s => s.Authorized);
        _miHeader.Text = $"ScreenExtend  ·  {_server.PrimaryUrl}";
        _miCode.Text = $"Pairing code: {code[..3]} {code[3..]}";
        _miPause.Checked = !_server.Hub.HostStreaming;
        _miControl.Checked = _server.InputEnabled;

        var displays = _server.Displays;
        bool driverOk = displays.Status == DriverStatus.Ok;
        _miDriver.Text = driverOk
            ? $"Virtual display driver: ready ({displays.Displays.Length} display{(displays.Displays.Length == 1 ? "" : "s")})"
            : displays.Status == DriverStatus.Error ? "Virtual display driver: error (see log)" : "Virtual display driver: not installed";
        _miInstall.Visible = !driverOk;
        _miInstall.Enabled = _server.Installer.State is not (InstallState.Downloading or InstallState.Installing);
        _miInstall.Text = _server.Installer.State switch
        {
            InstallState.Downloading => "Downloading driver…",
            InstallState.Installing => "Installing driver (approve the prompt)…",
            _ => "Install virtual display driver…",
        };
        _miDisplays.Visible = driverOk;
        _miDisplays.Enabled = displays.Displays.Length > 0;

        string tip = $"ScreenExtend · code {code}\n{connected} device{(connected == 1 ? "" : "s")} connected";
        if (!driverOk) tip += "\nDriver not installed: mirroring only";
        if (!_server.Hub.HostStreaming) tip += " · paused";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        // Disposing an item removes it from the collection, so take a copy before iterating.
        var oldItems = _miDevices.DropDownItems.Cast<ToolStripItem>().ToArray();
        _miDevices.DropDownItems.Clear();
        foreach (var old in oldItems) old.Dispose();
        var devices = _server.Pairing.Devices;
        if (devices.Length == 0)
            _miDevices.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        foreach (var d in devices)
        {
            string id = d.Id;
            var live = _server.Sessions.Values.FirstOrDefault(s => s.Authorized && s.PairedDevice?.Id == id);
            string suffix = live == null ? "" : live.Display != null ? $"  ({live.Display.Device.Replace(@"\\.\", "")})" : "  (connected)";
            var item = new ToolStripMenuItem(d.Name + suffix);
            item.DropDownItems.Add(new ToolStripMenuItem("Allow control", null, (_, _) => _server.SetDeviceControl(id, !d.AllowControl)) { Checked = d.AllowControl });
            item.DropDownItems.Add(new ToolStripMenuItem("Remove device", null, (_, _) => _server.RevokeDevice(id)));
            _miDevices.DropDownItems.Add(item);
        }
        _miRevokeAll.Enabled = devices.Length > 0;
    }

    private void InstallDriver()
    {
        var r = MessageBox.Show(
            "ScreenExtend uses the Parsec Virtual Display Driver (free, signed by Parsec Cloud, Inc.) to add virtual monitors.\n\n" +
            $"It will be downloaded from {DriverInstaller.DownloadUrl} and installed silently. Windows will ask for administrator permission.\n\nContinue?",
            "Install virtual display driver", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (r == DialogResult.OK) _server.Installer.Start();
    }

    private void ShowPairingInfo()
    {
        string code = _server.Pairing.Code;
        var urls = Network.Urls(_server.Port);
        string link = _server.PairingUrl(urls[0]);
        try { Clipboard.SetText(link); } catch (Exception ex) { Log.Debug($"Clipboard: {ex.Message}"); }
        string others = urls.Count > 1 ? "\n\nOther addresses of this PC:\n" + string.Join("\n", urls.Skip(1)) : "";
        MessageBox.Show(
            $"On your phone or tablet (same Wi-Fi), open:\n\n{urls[0]}\n\nand enter the pairing code:  {code}\n\n" +
            $"The direct pairing link was copied to the clipboard. \"Open in browser\" shows a QR code to scan.{others}",
            "ScreenExtend - connect a device", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RevokeAll()
    {
        var r = MessageBox.Show("Remove all paired devices? They will be disconnected and need a new pairing code.",
            "ScreenExtend", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r == DialogResult.OK) _server.RevokeAll();
    }

    /// <summary>Draws the tray icon (a monitor with a second, phone-shaped screen attached to its right) so no binary resource is needed.</summary>
    private static Icon CreateIcon()
    {
        int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var monitor = RoundedRect(new RectangleF(1 * s, 5 * s, 20 * s, 15 * s), 3 * s);
            using var monitorBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(34, 197, 160), Color.FromArgb(79, 140, 255));
            g.FillPath(monitorBrush, monitor);
            using var stand = new SolidBrush(Color.FromArgb(200, 210, 230));
            g.FillRectangle(stand, 8 * s, 21 * s, 6 * s, 2.5f * s);
            using var phone = RoundedRect(new RectangleF(21.5f * s, 9 * s, 9.5f * s, 17 * s), 2.5f * s);
            using var phoneFill = new SolidBrush(Color.FromArgb(20, 24, 33));
            using var phonePen = new Pen(Color.White, 2 * s);
            g.FillPath(phoneFill, phone);
            g.DrawPath(phonePen, phone);
            using var plus = new Pen(Color.FromArgb(34, 197, 160), 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(plus, 26.2f * s, 14 * s, 26.2f * s, 21 * s);
            g.DrawLine(plus, 22.8f * s, 17.5f * s, 29.7f * s, 17.5f * s);
        }
        IntPtr h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _appIcon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
