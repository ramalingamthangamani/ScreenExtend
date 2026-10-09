using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenExtend;

public enum InstallState { Idle, Downloading, Installing, Done, Failed }

/// <summary>
/// Downloads the Parsec Virtual Display Driver installer from Parsec's build server and runs it silently with
/// administrator rights (Windows shows the UAC prompt with the publisher). Runs at most one install at a time.
/// </summary>
public sealed class DriverInstaller
{
    public const string Version = "0.45.0.0";
    public const string DownloadUrl = "https://builds.parsec.app/vdd/parsec-vdd-" + Version + ".exe";
    private const long MinPlausibleSize = 500_000;

    private readonly string _dir;
    private readonly VirtualDisplayManager _displays;
    private int _running;

    public DriverInstaller(string dataDir, VirtualDisplayManager displays)
    {
        _dir = Path.Combine(dataDir, "driver");
        _displays = displays;
    }

    public InstallState State { get; private set; } = InstallState.Idle;
    public string Message { get; private set; } = "";
    public string InstallerPath => Path.Combine(_dir, $"parsec-vdd-{Version}.exe");

    public event Action? Changed;

    private void Set(InstallState s, string msg)
    {
        State = s;
        Message = msg;
        Log.Info($"Driver install: {s} {msg}");
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Error($"DriverInstaller.Changed handler failed: {ex.Message}"); }
    }

    /// <summary>Starts the download + install in the background; returns false when one is already running.</summary>
    public bool Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
        return true;
    }

    /// <summary>Downloads and installs; returns true on success. Used by <see cref="Start"/> and by the --install-driver option.</summary>
    public async Task<bool> RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            if (!File.Exists(InstallerPath) || new FileInfo(InstallerPath).Length < MinPlausibleSize)
            {
                Set(InstallState.Downloading, $"Downloading the virtual display driver ({DownloadUrl})…");
                string tmp = InstallerPath + ".part";
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenExtend/1.0");
                    using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                    await src.CopyToAsync(dst);
                }
                long size = new FileInfo(tmp).Length;
                if (size < MinPlausibleSize) throw new InvalidDataException($"Downloaded file is too small ({size} bytes).");
                if (!LooksLikeExe(tmp)) throw new InvalidDataException("Downloaded file is not a Windows executable.");
                File.Move(tmp, InstallerPath, overwrite: true);
            }

            Set(InstallState.Installing, "Installing… Approve the administrator prompt (publisher: Parsec Cloud, Inc.).");
            using var proc = Process.Start(new ProcessStartInfo(InstallerPath, "/S") { UseShellExecute = true, Verb = "runas" });
            if (proc == null) throw new InvalidOperationException("The installer did not start.");
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) throw new InvalidOperationException($"The installer exited with code {proc.ExitCode}.");

            // Device creation finishes a moment after the installer returns.
            for (int i = 0; i < 20; i++)
            {
                if (_displays.Refresh() == DriverStatus.Ok) break;
                await Task.Delay(500);
            }
            if (_displays.Status == DriverStatus.Ok)
            {
                Set(InstallState.Done, "The virtual display driver is installed.");
                return true;
            }
            Set(InstallState.Failed, _displays.StatusDetail.Length > 0 ? _displays.StatusDetail : "Installed, but the driver did not start. Restart Windows and try again.");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Set(InstallState.Failed, "Installation was cancelled at the administrator prompt.");
            return false;
        }
        catch (Exception ex)
        {
            Set(InstallState.Failed, $"Installation failed: {ex.Message}");
            return false;
        }
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
    }
}
