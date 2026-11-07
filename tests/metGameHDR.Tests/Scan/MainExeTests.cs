using MetGameHDR.Scan;

namespace metGameHDR.Tests.Scan;

public static class TestFs
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Touch(string dir, string name, int sizeMb)
    {
        Directory.CreateDirectory(dir);
        using var fs = new FileStream(Path.Combine(dir, name), FileMode.Create);
        fs.SetLength(sizeMb * 1024L * 1024L);
    }
}

public class MainExeTests
{
    [Fact]
    public void Pick_PrefersNameMatch()
    {
        var d = TestFs.TempDir(); TestFs.Touch(d, "CrashReport.exe", 148); TestFs.Touch(d, "PRAGMATA.exe", 36); TestFs.Touch(d, "InstallerMessage.exe", 1);
        Assert.Equal(Path.Combine(d, "PRAGMATA.exe"), MainExe.Pick(d, "PRAGMATA Deluxe Edition"));
    }

    [Fact]
    public void Pick_FallsBackToLargest()
    {
        var d = TestFs.TempDir(); TestFs.Touch(d, "launcher.exe", 2); TestFs.Touch(d, "tlou-ii.exe", 60); TestFs.Touch(d, "tlou-ii-l.exe", 61);
        Assert.Equal(Path.Combine(d, "tlou-ii-l.exe"), MainExe.Pick(d, "The Last of Us Part II Remastered"));
    }

    [Fact]
    public void Candidates_DropsServiceExes()
    {
        var d = TestFs.TempDir(); TestFs.Touch(d, "unins000.exe", 1); TestFs.Touch(d, "vcredist_x64.exe", 1); TestFs.Touch(d, "Game.exe", 1);
        Assert.Equal([Path.Combine(d, "Game.exe")], MainExe.Candidates(d));
    }
}
