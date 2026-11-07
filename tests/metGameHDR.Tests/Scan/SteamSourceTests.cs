using MetGameHDR.Scan;

namespace MetGameHDR.Tests.Scan;

public class SteamSourceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public SteamSourceTests() => Directory.CreateDirectory(Path.Combine(root, "steamapps"));

    public void Dispose() => Directory.Delete(root, true);

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(root, rel), text);

    string Libraries(params string[] paths) =>
        "\"libraryfolders\"\n{\n" + string.Concat(paths.Select((p, i) =>
            $"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{p.Replace("\\", "\\\\")}\"\n\t}}\n")) + "}\n";

    [Fact]
    public void Scan_ReadsManifestsFromAllLibraries()
    {
        var lib2 = Path.Combine(root, "lib2");
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        Write(@"steamapps\libraryfolders.vdf", Libraries(root, lib2));
        Write(@"steamapps\appmanifest_10.acf", "\"AppState\"\n{\n\t\"appid\"\t\"10\"\n\t\"name\"\t\"Half-Life\"\n\t\"installdir\"\t\"Half-Life\"\n}\n");
        File.WriteAllText(Path.Combine(lib2, @"steamapps\appmanifest_20.acf"), "\"AppState\" { \"appid\" \"20\" \"name\" \"Portal\" \"installdir\" \"Portal\" }");
        var games = new SteamSource(root).Scan().OrderBy(g => g.Name).ToList();
        Assert.Equal(2, games.Count);
        Assert.Equal("Half-Life", games[0].Name);
        Assert.Equal(Path.Combine(root, "steamapps", "common", "Half-Life"), games[0].InstallDir);
        Assert.Equal(GameSource.Steam, games[0].Source);
        Assert.Null(games[0].MainExe);
        Assert.Equal(Path.Combine(lib2, "steamapps", "common", "Portal"), games[1].InstallDir);
    }

    [Fact]
    public void Scan_FillsSteamAppId()
    {
        var lib2 = Path.Combine(root, "lib2");
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        Write(@"steamapps\libraryfolders.vdf", Libraries(root, lib2));
        Write(@"steamapps\appmanifest_10.acf", "\"AppState\"\n{\n\t\"appid\"\t\"10\"\n\t\"name\"\t\"Half-Life\"\n\t\"installdir\"\t\"Half-Life\"\n}\n");
        Write(@"steamapps\appmanifest_30.acf", "\"AppState\" { \"name\" \"NoId\" \"installdir\" \"NoId\" }");
        File.WriteAllText(Path.Combine(lib2, @"steamapps\appmanifest_20.acf"), "\"AppState\" { \"appid\" \"20\" \"name\" \"Portal\" \"installdir\" \"Portal\" }");
        var games = new SteamSource(root).Scan().ToDictionary(g => g.Name);
        Assert.Equal("10", games["Half-Life"].SteamAppId);
        Assert.Equal("20", games["Portal"].SteamAppId);
        Assert.Null(games["NoId"].SteamAppId);
    }

    [Fact]
    public void Scan_NullPathOrMissingFiles_ReturnsEmpty()
    {
        Assert.Empty(new SteamSource(null).Scan());
        Assert.Empty(new SteamSource(Path.Combine(root, "nope")).Scan());
        Assert.Empty(new SteamSource(root).Scan());
    }

    [Fact]
    public void Scan_SkipsBrokenAndIncompleteManifests()
    {
        Write(@"steamapps\libraryfolders.vdf", Libraries(root));
        Write(@"steamapps\appmanifest_1.acf", "\"AppState\" { \"name\" \"NoDir\" }");
        Write(@"steamapps\appmanifest_2.acf", "\"AppState\" { \"name\" \"Broken\" ");
        Write(@"steamapps\appmanifest_3.acf", "\"AppState\" { \"name\" \"Ok\" \"installdir\" \"Ok\" }");
        Assert.Equal("Ok", new SteamSource(root).Scan().Single().Name);
    }
}
