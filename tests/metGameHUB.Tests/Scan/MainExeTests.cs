using MetGameHUB.Scan;

namespace metGameHUB.Tests.Scan;

public abstract class TempFsTest : IDisposable
{
    private readonly List<string> _dirs = [];

    protected string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    protected static void Touch(string dir, string name, int sizeMb)
    {
        Directory.CreateDirectory(dir);
        using var fs = new FileStream(Path.Combine(dir, name), FileMode.Create);
        fs.SetLength(sizeMb * 1024L * 1024L);
    }

    public virtual void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class MainExeTests : TempFsTest
{
    [Fact]
    public void Pick_PrefersNameMatch()
    {
        var d = TempDir(); Touch(d, "CrashReport.exe", 148); Touch(d, "PRAGMATA.exe", 36); Touch(d, "InstallerMessage.exe", 1);
        Assert.Equal(Path.Combine(d, "PRAGMATA.exe"), MainExe.Pick(d, "PRAGMATA Deluxe Edition"));
    }

    [Fact]
    public void Pick_NameMatchBeatsLargerFile()
    {
        var d = TempDir(); Touch(d, "Control.exe", 1); Touch(d, "launcher2.exe", 50);
        Assert.Equal(Path.Combine(d, "Control.exe"), MainExe.Pick(d, "Control"));
    }

    [Fact]
    public void Pick_NonMatchingSmallLosesToLarger()
    {
        var d = TempDir(); Touch(d, "tool.exe", 1); Touch(d, "biggame.exe", 50);
        Assert.Equal(Path.Combine(d, "biggame.exe"), MainExe.Pick(d, "Something Else"));
    }

    [Fact]
    public void Pick_FallsBackToLargest()
    {
        var d = TempDir(); Touch(d, "launcher.exe", 2); Touch(d, "tlou-ii.exe", 60); Touch(d, "tlou-ii-l.exe", 61);
        Assert.Equal(Path.Combine(d, "tlou-ii-l.exe"), MainExe.Pick(d, "The Last of Us Part II Remastered"));
    }

    [Fact]
    public void Candidates_DropsServiceExes()
    {
        var d = TempDir(); Touch(d, "unins000.exe", 1); Touch(d, "vcredist_x64.exe", 1); Touch(d, "Game.exe", 1);
        Assert.Equal([Path.Combine(d, "Game.exe")], MainExe.Candidates(d));
    }
}
