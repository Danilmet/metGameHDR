using MetGameHUB.Scan;

namespace metGameHUB.Tests.Scan;

public class LibraryScannerTests
{
    private sealed class Boom : IGameSource { public GameSource Source => GameSource.Gog; public IEnumerable<Game> Scan() => throw new IOException("reg"); }
    private sealed class Fixed(params Game[] g) : IGameSource { public GameSource Source => GameSource.Folder; public IEnumerable<Game> Scan() => g; }

    [Fact]
    public void Scan_IsolatesSourceFailuresAndDedupes()
    {
        var logs = new List<string>();
        var a = new Game("A", @"C:\A", GameSource.Folder, null);
        var games = LibraryScanner.Scan([new Boom(), new Fixed(a), new Fixed(a with { Name = "A2" })], logs.Add);
        Assert.Equal(["A"], games.Select(g => g.Name));
        Assert.Contains(logs, l => l.StartsWith("Gog:"));
    }

    [Fact]
    public void Scan_TrimsTrailingSeparatorBeforeDedupe()
    {
        var games = LibraryScanner.Scan([new Fixed(new Game("EA", @"C:\Games\EA\", GameSource.Ea, null), new Game("Dup", @"C:\Games\EA", GameSource.Folder, null))], _ => { });
        Assert.Equal(@"C:\Games\EA", Assert.Single(games).InstallDir);
    }

    [Fact]
    public void AppsWithSelf_AddsOnlyHubExe()
    {
        string[] apps = [@"C:\Tools\a.exe"];
        Assert.Equal([@"C:\Tools\a.exe", @"C:\Hub\metGameHUB.exe"], LibraryScanner.AppsWithSelf(apps, @"C:\Hub\metGameHUB.exe"));
        Assert.Equal(apps, LibraryScanner.AppsWithSelf(apps, @"C:\dotnet\dotnet.exe"));
        Assert.Equal(apps, LibraryScanner.AppsWithSelf(apps, null));
    }
}
