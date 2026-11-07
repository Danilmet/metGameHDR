using MetGameHUB.Scan;

namespace MetGameHUB.Tests.Scan;

public class XboxSourceTests
{
    [Fact]
    public void ParseDisplayName_ReadsShellVisuals()
    {
        var xml = """<?xml version="1.0"?><Game configVersion="1"><ShellVisuals DefaultDisplayName="Forza Horizon 5" /></Game>""";
        Assert.Equal("Forza Horizon 5", XboxSource.ParseDisplayName(xml, "x"));
        Assert.Equal("x", XboxSource.ParseDisplayName("<Game/>", "x"));
        Assert.Equal("x", XboxSource.ParseDisplayName("<broken", "x"));
    }
}
