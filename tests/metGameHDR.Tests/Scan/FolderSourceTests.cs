using MetGameHDR.Scan;

namespace metGameHDR.Tests.Scan;

public class FolderSourceTests
{
    [Fact]
    public void Scan_SkipsFoldersWithoutExe()
    {
        var root = TestFs.TempDir(); Directory.CreateDirectory(Path.Combine(root, "Saves")); var g = Path.Combine(root, "Control"); TestFs.Touch(g, "Control.exe", 1);
        var games = new FolderSource([root, @"C:\nope\" + Guid.NewGuid()]).Scan().ToList();
        Assert.Single(games); Assert.Equal("Control", games[0].Name); Assert.Equal(Path.Combine(g, "Control.exe"), games[0].MainExe);
    }
}
