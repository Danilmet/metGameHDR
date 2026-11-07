using MetGameHDR.Advice;

namespace MetGameHDR.Tests;

public class HardwareTests
{
    [Fact]
    public void Detect_DoesNotThrow_AndFillsMonitor()
    {
        var info = Hardware.Detect();

        Assert.False(string.IsNullOrWhiteSpace(info.Gpu));
        Assert.False(string.IsNullOrWhiteSpace(info.Cpu));
        if (Environment.UserInteractive)
        {
            Assert.True(info.Hz > 0);
            Assert.True(info.Width > 0);
        }
    }
}
