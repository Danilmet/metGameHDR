using System.IO;
using MetGameHUB.Scan;
using MetGameHUB.Steam;

namespace MetGameHUB.Tests.Steam;

public class SteamAdderTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact] public void StatusOf_DistinguishesThreeStates()
    {
        var grid = TempDir(); var root = ShortcutsFile.Load(Path.Combine(grid, "none.vdf"));
        var g = new Game("G", @"C:\G", GameSource.Folder, @"C:\G\g.exe");
        Assert.Equal(SteamStatus.NotAdded, SteamAdder.StatusOf(g, root, grid));
        var e = ShortcutsFile.Upsert(root, "G", @"C:\G\g.exe", @"C:\G", "");
        Assert.Equal(SteamStatus.NoArtwork, SteamAdder.StatusOf(g, root, grid));
        File.WriteAllBytes(Path.Combine(grid, ShortcutsFile.AppIdOf(e) + "p.png"), [1]);
        Assert.Equal(SteamStatus.Added, SteamAdder.StatusOf(g, root, grid));
    }
}
