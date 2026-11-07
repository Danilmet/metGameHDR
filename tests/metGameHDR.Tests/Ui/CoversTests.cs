using MetGameHDR.Scan;
using MetGameHDR.Steam;
using MetGameHDR.Ui;

namespace MetGameHDR.Tests.Ui;

public class CoversTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    string Cache => Path.Combine(root, "cache");

    public CoversTests() => Directory.CreateDirectory(Cache);

    public void Dispose() => Directory.Delete(root, true);

    string Touch(string rel)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    static Game Steam10() => new("Half-Life", @"C:\Steam\steamapps\common\Half-Life", GameSource.Steam, null, null, "10");

    [Fact]
    public void LocalPath_SteamNewLayout()
    {
        var file = Touch(@"appcache\librarycache\10\library_600x900.jpg");
        Assert.Equal(file, Covers.LocalPath(Steam10(), root, null, null, Cache));
    }

    [Fact]
    public void LocalPath_SteamOldLayout()
    {
        var file = Touch(@"appcache\librarycache\10_library_600x900.jpg");
        Assert.Equal(file, Covers.LocalPath(Steam10(), root, null, null, Cache));
    }

    [Fact]
    public void LocalPath_SteamWithoutCache_ReturnsNull() =>
        Assert.Null(Covers.LocalPath(Steam10(), root, null, null, Cache));

    [Fact]
    public void LocalPath_FolderAddedToSteam_UsesGrid()
    {
        var dir = @"C:\Games\Foo";
        var shortcuts = new VdfObject();
        var appId = ShortcutsFile.AppIdOf(ShortcutsFile.Upsert(shortcuts, "Foo", dir + @"\foo.exe", dir, ""));
        var grid = Path.Combine(root, "grid");
        var file = Touch($@"grid\{appId}p.png");
        var game = new Game("Foo", dir, GameSource.Folder, null);
        Assert.Equal(file, Covers.LocalPath(game, null, shortcuts, grid, Cache));
    }

    [Fact]
    public void LocalPath_FallsBackToCache()
    {
        var game = new Game("Foo", @"C:\Epic\Foo", GameSource.Epic, null);
        var file = Touch(@"cache\" + Covers.CacheFileName(game.InstallDir) + ".jpg");
        Assert.Equal(file, Covers.LocalPath(game, null, null, null, Cache));
    }

    [Fact]
    public void CacheFileName_SafeAndStable()
    {
        var name = Covers.CacheFileName(@"C:\Games\Foo: Bar?");
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('?', name);
        Assert.True(name.Length <= 69);
        Assert.Equal(name, Covers.CacheFileName(@"C:\Games\Foo: Bar?"));
        Assert.Equal(name, Covers.CacheFileName(@"c:\games\foo: bar?"));
        Assert.NotEqual(name, Covers.CacheFileName(@"C:\Games\Other"));
    }
}
