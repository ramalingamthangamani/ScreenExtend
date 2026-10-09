using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenExtend;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.NoTray || options.InstallDriver || options.DriverTest) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        // Must happen before any window or capture: coordinates everywhere are physical pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Log.Init(options.DataDir, options.Verbose);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled exception: {e.ExceptionObject}");
        Application.ThreadException += (_, e) =>
        {
            Log.Error($"UI thread exception: {e.Exception}");
            MessageBox.Show(e.Exception.Message, "ScreenExtend", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        if (options.DriverTest) return VirtualDisplayManager.DriverTest(Console.Out);

        if (options.InstallDriver)
        {
            using var displays = new VirtualDisplayManager();
            if (displays.Status == DriverStatus.Ok)
            {
                Console.WriteLine("The virtual display driver is already installed.");
                return 0;
            }
            var installer = new DriverInstaller(options.DataDir, displays);
            installer.Changed += () => Console.WriteLine(installer.Message);
            bool ok = installer.RunAsync().GetAwaiter().GetResult();
            return ok ? 0 : 1;
        }

        // One tray instance per data directory; a second launch just reopens the running one's page.
        string portFile = Path.Combine(options.DataDir, "port.txt");
        using var instance = new Mutex(true, @"Local\ScreenExtend." + StableHash(options.DataDir), out bool firstInstance);
        if (!firstInstance)
        {
            Log.Info("ScreenExtend is already running");
            if (!options.NoBrowser && TryReadPort(portFile, out int running)) TrayApp.OpenBrowser($"http://localhost:{running}/connect");
            else if (!options.NoTray) MessageBox.Show("ScreenExtend is already running. Use its icon in the notification area.", "ScreenExtend");
            return 0;
        }

        var server = new AppServer(options);
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            if (!options.NoTray)
                MessageBox.Show(ex.Message, "ScreenExtend", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try { File.WriteAllText(portFile, server.Port.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Log.Info($"ScreenExtend listening on port {server.Port}");
        foreach (string url in Network.Urls(server.Port)) Log.Info($"Open on your phone: {url}");
        Log.Info($"Pairing code: {server.Pairing.Code}  (connection page with QR code: {server.ConnectPageUrl})");
        if (server.Displays.Status != DriverStatus.Ok)
            Log.Warn($"Virtual display driver not available: {server.Displays.StatusDetail} Devices will mirror the main display until it is installed (connection page or --install-driver).");
        if (!options.NoBrowser) TrayApp.OpenBrowser(server.ConnectPageUrl);

        if (options.NoTray)
        {
            using var exit = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { exit.Set(); } catch (ObjectDisposedException) { } };
            exit.Wait();
        }
        else
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var tray = new TrayApp(server);
            Application.Run(tray);
        }

        server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        try { File.Delete(portFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Log.Info("ScreenExtend stopped");
        return 0;
    }

    private static bool TryReadPort(string file, out int port)
    {
        port = 0;
        try { return int.TryParse(File.ReadAllText(file).Trim(), out port) && port > 0 && port < 65536; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return h.ToString("x8");
    }
}
