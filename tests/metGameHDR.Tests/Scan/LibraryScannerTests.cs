using MetGameHDR.Scan;

namespace metGameHDR.Tests.Scan;

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
}
