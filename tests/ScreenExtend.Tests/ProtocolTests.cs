using System.Text.Json;
using ScreenExtend;

public class ProtocolTests
{
    [Theory]
    [InlineData("low", StreamQuality.Low, true)]
    [InlineData("HIGH", StreamQuality.High, true)]
    [InlineData("balanced", StreamQuality.Balanced, true)]
    [InlineData("native", StreamQuality.Native, true)]
    [InlineData("ultra", StreamQuality.Balanced, false)]
    [InlineData(null, StreamQuality.Balanced, false)]
    public void QualityPresetParse(string? s, StreamQuality expected, bool ok)
    {
        Assert.Equal(ok, QualityPreset.TryParse(s, out var q));
        Assert.Equal(expected, q);
        Assert.Equal(expected.ToString().ToLowerInvariant(), QualityPreset.Name(expected));
    }

    [Fact]
    public void QualityPresetsAreOrdered()
    {
        var low = QualityPreset.For(StreamQuality.Low);
        var mid = QualityPreset.For(StreamQuality.Balanced);
        var high = QualityPreset.For(StreamQuality.High);
        var native = QualityPreset.For(StreamQuality.Native);
        Assert.True(low.MaxDim < mid.MaxDim && mid.MaxDim < high.MaxDim && high.MaxDim < native.MaxDim);
        Assert.True(low.Jpeg <= mid.Jpeg && mid.Jpeg <= high.Jpeg && high.Jpeg <= native.Jpeg);
        Assert.All(new[] { low, mid, high, native }, p => Assert.True(p.Refine > p.Jpeg));
    }

    [Fact]
    public void NativePresetKeepsA1440pDisplayUnscaled()
    {
        var mon = new MonitorInfo(1, @"\\.\DISPLAY3", 1920, 0, 2560, 1440, false);
        Assert.Equal((2560, 1440), StreamSource.TargetSize(mon, QualityPreset.For(StreamQuality.Native).MaxDim));
        Assert.Equal((1920, 1080), StreamSource.TargetSize(mon, QualityPreset.For(StreamQuality.High).MaxDim));
    }

    [Fact]
    public void TargetSizeKeepsPortraitAspectAndEvenDimensions()
    {
        var mon = new MonitorInfo(1, @"\\.\DISPLAY3", 0, 0, 1080, 1920, false);
        var (w, h) = StreamSource.TargetSize(mon, 1280);
        Assert.Equal(1280, h);
        Assert.Equal(720, w);
        Assert.Equal(0, w % 2);
    }

    [Fact]
    public void PadRegionExpandsToEvenCoordinatesInsideTheFrame()
    {
        var r = StreamSource.PadRegion(new DirtyRect(101, 51, 10, 7), 1920, 1080, pad: 4);
        Assert.Equal(0, r.X % 2);
        Assert.Equal(0, r.Y % 2);
        Assert.True(r.X <= 97 && r.Y <= 47);
        Assert.True(r.X + r.Width >= 115 && r.Y + r.Height >= 62);
        Assert.True(r.X + r.Width <= 1920 && r.Y + r.Height <= 1080);

        var edge = StreamSource.PadRegion(new DirtyRect(1915, 1075, 5, 5), 1920, 1080, pad: 4);
        Assert.Equal(1920, edge.X + edge.Width);
        Assert.Equal(1080, edge.Y + edge.Height);
        Assert.True(edge.X >= 0 && edge.Y >= 0);
    }

    [Fact]
    public void FrameHeaderIsThirtySixBytes()
    {
        Assert.Equal(36, EncodedFrame.HeaderSize);
        Assert.Equal(4, EncodedFrame.FlagPartial);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  My Phone  ", "My Phone")]
    [InlineData("a\u0000b\nc", "abc")]
    public void SanitizeStripsControlCharsAndTrims(string? input, string expected) =>
        Assert.Equal(expected, ClientSession.Sanitize(input, 40));

    [Fact]
    public void SanitizeTruncates() =>
        Assert.Equal(new string('x', 5), ClientSession.Sanitize(new string('x', 50), 5));

    [Fact]
    public void JsonHelpersAreLenient()
    {
        using var doc = JsonDocument.Parse("""{"s":"txt","n":1.5,"i":7,"big":1e12,"b":true,"nb":"yes","x":null}""");
        var e = doc.RootElement;
        Assert.True(e.Has("s"));
        Assert.False(e.Has("missing"));
        Assert.Equal("txt", e.Str("s"));
        Assert.Null(e.Str("n"));
        Assert.Equal(1.5, e.Num("n"));
        Assert.Equal(9, e.Num("s", 9));
        Assert.Equal(7, e.Int("i"));
        Assert.Equal(1, e.Int("n"));
        Assert.Equal(int.MaxValue, e.Int("big"));
        Assert.Equal(-1, e.Int("missing", -1));
        Assert.True(e.Bool("b"));
        Assert.True(e.Bool("nb", true));
        Assert.False(e.Bool("x"));
    }

    [Fact]
    public void MonitorListExposesIndexSizeAndVirtualFlag()
    {
        var list = ClientSession.MonitorList(new List<MonitorInfo>
        {
            new(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
            new(1, @"\\.\DISPLAY2", 1920, 0, 2560, 1440, false) { Virtual = true },
        });
        string json = JsonSerializer.Serialize(list);
        Assert.Contains("\"i\":0", json);
        Assert.Contains("\"w\":1920", json);
        Assert.Contains("\"primary\":true", json);
        Assert.Contains("\"virtual\":true", json);
        Assert.Contains("Virtual display 2 - 2560x1440", json);
        Assert.Contains("\"device\":\"\\\\\\\\.\\\\DISPLAY2\"", json);
    }

    [Fact]
    public void ServerClockIsMonotonicAndNearWallClock()
    {
        double a = ServerClock.NowMs;
        Thread.Sleep(5);
        double b = ServerClock.NowMs;
        Assert.True(b > a);
        Assert.True(Math.Abs(b - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 5000);
    }

    [Fact]
    public void MonitorsEnumerateAtLeastOneWithWorkArea()
    {
        var all = Monitors.GetAll();
        Assert.NotEmpty(all);
        Assert.True(all[0].Primary);
        Assert.True(all[0].WorkWidth > 0 && all[0].WorkHeight > 0);
        Assert.Equal(all[0], Monitors.Find(all[0].Device));
        Assert.Null(Monitors.Find(@"\\.\DISPLAY99"));
    }
}
