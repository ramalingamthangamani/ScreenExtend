using ScreenExtend;

public class ScreenCapturerTests
{
    [Fact]
    public void FirstCaptureIsFullyDirtyAndStaticScreenIsNotDirtyTwice()
    {
        var mon = Monitors.Primary();
        using var cap = new ScreenCapturer();
        if (!cap.Capture(mon, 320, 180, drawCursor: false)) return; // secure desktop / no session: nothing to test
        var first = cap.Diff();
        Assert.Equal(new DirtyRect(0, 0, 320, 180), first);
        Assert.False(first.IsEmpty);
        Assert.Equal(320L * 180, first.Area);

        // Without a new capture the pixels are identical to the stored copy, so nothing is dirty.
        var second = cap.Diff();
        Assert.True(second.IsEmpty);

        byte[] full = cap.Encode(60, EncodedFrame.HeaderSize);
        Assert.True(full.Length > EncodedFrame.HeaderSize + 100);
        Assert.Equal(0xFF, full[EncodedFrame.HeaderSize]);     // JPEG SOI marker
        Assert.Equal(0xD8, full[EncodedFrame.HeaderSize + 1]);

        byte[] part = cap.EncodeRegion(new DirtyRect(10, 20, 64, 32), 60, EncodedFrame.HeaderSize);
        Assert.True(part.Length > EncodedFrame.HeaderSize + 50);
        Assert.True(part.Length < full.Length);
    }
}
