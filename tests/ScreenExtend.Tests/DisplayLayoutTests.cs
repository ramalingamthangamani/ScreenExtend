using ScreenExtend;

public class DisplayLayoutTests
{
    // Modes the Parsec driver typically offers.
    private static readonly DisplayMode[] Modes =
    {
        new(640, 480, 60), new(800, 600, 60), new(1024, 768, 60), new(1280, 720, 60), new(1280, 800, 60), new(1366, 768, 60),
        new(1600, 900, 60), new(1680, 1050, 60), new(1920, 1080, 60), new(1920, 1200, 60), new(2560, 1080, 60),
        new(2560, 1440, 60), new(2560, 1600, 60), new(3440, 1440, 60), new(3840, 2160, 60),
    };

    [Fact]
    public void PhoneWithTallAspectGetsUltrawideClassModeWithinCap()
    {
        // 20:9 phone (2400x1080 physical), default cap 1920 long side: 1920x1080 (16:9) is the closest size within the cap;
        // 2560x1080 (21:9) matches the aspect better but exceeds the cap.
        var req = new DisplayRequest(2400, 1080, Portrait: false, MaxLongSide: 1920, ScalePercent: 0);
        var m = DisplayLayout.ChooseMode(Modes, req);
        Assert.NotNull(m);
        Assert.True(m!.Long <= 1920 * 1.05);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
    }

    [Fact]
    public void PhoneWithHigherCapGetsUltrawide()
    {
        var req = new DisplayRequest(2400, 1080, false, 2560, 0);
        var m = DisplayLayout.ChooseMode(Modes, req);
        Assert.Equal((2560, 1080), (m!.Width, m.Height));
    }

    [Fact]
    public void TabletSixteenTenGetsSixteenTen()
    {
        var req = new DisplayRequest(2560, 1600, false, 1920, 0);
        var m = DisplayLayout.ChooseMode(Modes, req);
        Assert.Equal((1920, 1200), (m!.Width, m.Height));
    }

    [Fact]
    public void ZeroCapMeansLargestMatchingAspect()
    {
        var req = new DisplayRequest(3840, 2160, false, 0, 0);
        var m = DisplayLayout.ChooseMode(Modes, req);
        Assert.Equal((3840, 2160), (m!.Width, m.Height));
    }

    [Fact]
    public void LowCapPicks720pClass()
    {
        var req = new DisplayRequest(2340, 1080, false, 1280, 0);
        var m = DisplayLayout.ChooseMode(Modes, req);
        Assert.Equal((1280, 720), (m!.Width, m.Height));
    }

    [Fact]
    public void PortraitFlagDoesNotChangeTheModeOnlyTheOrientation()
    {
        var a = DisplayLayout.ChooseMode(Modes, new DisplayRequest(2400, 1080, false, 1920, 0));
        var b = DisplayLayout.ChooseMode(Modes, new DisplayRequest(2400, 1080, true, 1920, 0));
        Assert.Equal(a, b);
    }

    [Fact]
    public void UnknownScreenFallsBackTo1080p()
    {
        var m = DisplayLayout.ChooseMode(Modes, new DisplayRequest(0, 0, false, 1920, 0));
        Assert.Equal((1920, 1080), (m!.Width, m.Height));
    }

    [Fact]
    public void EmptyModeListGivesNull() =>
        Assert.Null(DisplayLayout.ChooseMode(Array.Empty<DisplayMode>(), DisplayRequest.Default));

    [Theory]
    [InlineData(100, 100)]
    [InlineData(112, 100)]
    [InlineData(113, 125)]
    [InlineData(160, 150)]
    [InlineData(190, 200)]
    [InlineData(400, 250)] // clamped: anything above 250 % is unusable on a phone-sized display
    public void SnapRoundsToWindowsScaleSteps(int input, int expected) => Assert.Equal(expected, DisplayLayout.Snap(input));

    [Fact]
    public void AutoScaleIsLargeForPhonesAndModestForTablets()
    {
        var mode = new DisplayMode(1920, 1080, 60);
        // Phone: 2400x1080 physical at dpr 2.625 -> 412 CSS px short side.
        int phone = DisplayLayout.AutoScale(mode, new DisplayRequest(2400, 1080, false, 1920, 0), 2.625);
        // Tablet: 2560x1600 at dpr 2 -> 800 CSS px short side.
        int tablet = DisplayLayout.AutoScale(mode, new DisplayRequest(2560, 1600, false, 1920, 0), 2.0);
        Assert.True(phone >= 175 && phone <= 250, $"phone scale {phone}");
        Assert.True(tablet >= 100 && tablet <= 125, $"tablet scale {tablet}");
        Assert.True(phone > tablet);
    }

    [Fact]
    public void ResolutionPresetsAreAscending()
    {
        for (int i = 1; i < DisplayRequest.ResolutionPresets.Length; i++)
            Assert.True(DisplayRequest.ResolutionPresets[i] > DisplayRequest.ResolutionPresets[i - 1]);
    }
}
