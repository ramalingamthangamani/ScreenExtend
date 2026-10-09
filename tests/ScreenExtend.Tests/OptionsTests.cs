using ScreenExtend;

public class OptionsTests
{
    [Fact]
    public void DefaultsAreSensible()
    {
        var o = Options.Parse(Array.Empty<string>());
        Assert.Equal(8700, o.Port);
        Assert.False(o.PortExplicit);
        Assert.False(o.NoTray);
        Assert.False(o.NoBrowser);
        Assert.False(o.InstallDriver);
        Assert.Null(o.WebRoot);
        Assert.EndsWith("ScreenExtend", o.DataDir);
    }

    [Fact]
    public void ParsesAllOptions()
    {
        var o = Options.Parse(new[] { "--port", "9000", "--no-tray", "--no-browser", "-v", "--install-driver", "--data-dir", "C:\\tmp\\se", "--webroot", "web" });
        Assert.Equal(9000, o.Port);
        Assert.True(o.PortExplicit);
        Assert.True(o.NoTray);
        Assert.True(o.NoBrowser);
        Assert.True(o.Verbose);
        Assert.True(o.InstallDriver);
        Assert.Equal("C:\\tmp\\se", o.DataDir);
        Assert.True(Path.IsPathRooted(o.WebRoot));
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "abc")]
    [InlineData("--bogus")]
    [InlineData("--help")]
    public void RejectsBadArguments(params string[] args) => Assert.Throws<ArgumentException>(() => Options.Parse(args));
}
