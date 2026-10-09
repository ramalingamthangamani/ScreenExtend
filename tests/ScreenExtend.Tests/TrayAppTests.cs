using ScreenExtend;

public class TrayAppTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void MenuCanBeBuiltAndRebuiltWithDevices()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ScreenExtendTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RunSta(() =>
            {
                var server = new AppServer(Options.Parse(new[] { "--data-dir", dir, "--no-browser" }));
                try
                {
                    using var tray = new TrayApp(server);
                    server.Pairing.TryPair(server.Pairing.Code, "Phone", out _, out var d);
                    server.Pairing.TryPair(server.Pairing.Code, "Tablet", out _, out _);
                    tray.RefreshMenu();
                    tray.RefreshMenu();
                    server.Pairing.Revoke(d!.Id);
                    tray.RefreshMenu();
                }
                finally
                {
                    server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DriverStatusIsReportedWithoutThrowing()
    {
        using var displays = new VirtualDisplayManager();
        Assert.True(Enum.IsDefined(displays.Status));
        if (displays.Status != DriverStatus.Ok) Assert.False(string.IsNullOrEmpty(displays.StatusDetail));
        Assert.Empty(displays.Displays);
    }
}
