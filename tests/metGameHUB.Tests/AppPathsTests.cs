namespace MetGameHUB.Tests;

public class AppPathsTests
{
    [Fact]
    public void MoveLegacy_OldExistsNewMissing_Moves()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var oldDir = Path.Combine(root, "metGameHDR");
            var newDir = Path.Combine(root, "metGameHUB");
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{}");
            AppPaths.MoveLegacy(oldDir, newDir);
            Assert.False(Directory.Exists(oldDir));
            Assert.True(File.Exists(Path.Combine(newDir, "settings.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MoveLegacy_NewExists_LeavesBoth()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var oldDir = Path.Combine(root, "metGameHDR");
            var newDir = Path.Combine(root, "metGameHUB");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);
            AppPaths.MoveLegacy(oldDir, newDir);
            Assert.True(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(newDir));
        }
        finally { Directory.Delete(root, true); }
    }
}
