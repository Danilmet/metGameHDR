using MetGameHUB.Scan;

namespace MetGameHUB.Tests.Scan;

public class EpicSourceTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Scan_ParsesItemManifests()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "a.item"), """{"DisplayName":"Fortnite","InstallLocation":"C:\\Epic\\Fortnite","LaunchExecutable":"FortniteGame\\Binaries\\Win64\\Fortnite.exe"}""");
        var g = new EpicSource(dir).Scan().Single();
        Assert.Equal("Fortnite", g.Name);
        Assert.Equal(GameSource.Epic, g.Source);
        Assert.Equal(@"C:\Epic\Fortnite\FortniteGame\Binaries\Win64\Fortnite.exe", g.MainExe);
    }

    [Fact]
    public void Scan_MissingDir_ReturnsEmpty() => Assert.Empty(new EpicSource(@"C:\nope\" + Guid.NewGuid()).Scan());

    [Fact]
    public void Scan_SkipsBrokenAndIncompleteManifests_TrimsTrailingSlash()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "bad.item"), "{not json");
        File.WriteAllText(Path.Combine(dir, "partial.item"), """{"DisplayName":"X"}""");
        File.WriteAllText(Path.Combine(dir, "ok.item"), """{"DisplayName":"Y","InstallLocation":"C:\\Epic\\Y\\","LaunchExecutable":"y.exe"}""");
        var g = new EpicSource(dir).Scan().Single();
        Assert.Equal(@"C:\Epic\Y", g.InstallDir);
        Assert.Equal(@"C:\Epic\Y\y.exe", g.MainExe);
    }
}
