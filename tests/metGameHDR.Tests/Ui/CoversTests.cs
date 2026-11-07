using System.Net;
using System.Net.Http;
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

    sealed class Fake(params (string urlSuffix, byte[] body)[] routes) : HttpMessageHandler
    {
        public int Searches;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.OriginalString;
            if (url.Contains("search/autocomplete/")) Interlocked.Increment(ref Searches);
            await Task.Delay(20, ct);
            foreach (var (suffix, body) in routes)
                if (url.EndsWith(suffix))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    static readonly byte[] ImageBytes = [9, 8, 7];

    static Fake GridFake() => new(
        ("search/autocomplete/Foo", Utf8("""{"success":true,"data":[{"id":42}]}""")),
        ("grids/game/42?dimensions=600x900", Utf8("""{"success":true,"data":[{"score":1,"url":"http://img/x.png"}]}""")),
        ("http://img/x.png", ImageBytes));

    static Game Foo() => new("Foo", @"C:\Epic\Foo", GameSource.Epic, null);

    [Fact]
    public async Task FetchAsync_SavesBestGrid()
    {
        var http = new HttpClient(GridFake());
        var path = await new Covers(http, Cache).FetchAsync(Foo(), "Foo", new SteamGridDb(http, "k"));
        var expected = Path.Combine(Cache, Covers.CacheFileName(Foo().InstallDir) + ".png");
        Assert.Equal(expected, path);
        Assert.Equal(ImageBytes, File.ReadAllBytes(expected));
    }

    [Fact]
    public async Task FetchAsync_NoMatch_ReturnsNull()
    {
        var http = new HttpClient(new Fake(("search/autocomplete/Foo", Utf8("""{"success":true,"data":[]}"""))));
        Assert.Null(await new Covers(http, Cache).FetchAsync(Foo(), "Foo", new SteamGridDb(http, "k")));
        Assert.Empty(Directory.EnumerateFiles(Cache));
    }

    [Fact]
    public async Task FetchAsync_SameGameTwice_DownloadsOnce()
    {
        var fake = GridFake();
        var http = new HttpClient(fake);
        var covers = new Covers(http, Cache);
        var db = new SteamGridDb(http, "k");
        await Task.WhenAll(covers.FetchAsync(Foo(), "Foo", db), covers.FetchAsync(Foo(), "Foo", db));
        Assert.Equal(1, fake.Searches);
    }

    [Fact]
    public void LoadImage_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(root, "bad.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Null(Covers.LoadImage(path));
    }
}
