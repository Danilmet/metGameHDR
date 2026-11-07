using MetGameHDR.Advice;
using MetGameHDR.Scan;

namespace MetGameHDR.Tests;

public class ConfigFinderTests
{
    private static Game MakeGame(string name, string installDir = "", string? exe = null) =>
        new(name, installDir, GameSource.Steam, exe);

    private static void WithTemp(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('a', bytes));
    }

    private static FinderRoots Roots(string dir) =>
        new(Path.Combine(dir, "local"), Path.Combine(dir, "docs"), Path.Combine(dir, "saved"));

    [Fact]
    public void Find_UnrealLayout_ReturnsGameUserSettings()
    {
        WithTemp(dir =>
        {
            var roots = Roots(dir);
            var install = Path.Combine(dir, "Games", "Foo");
            Directory.CreateDirectory(install);
            var ini = Path.Combine(roots.LocalAppData, "Foo", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
            Write(ini, 100);

            var found = ConfigFinder.Find(MakeGame("Foo: Bar", install), roots);

            var c = Assert.Single(found);
            Assert.Equal(ini, c.Path);
            Assert.False(c.Truncated);
        });
    }

    [Fact]
    public void NameMatches_IgnoresCaseAndPunctuation()
    {
        Assert.True(ConfigFinder.NameMatches("foobar2", MakeGame("Foo-Bar 2")));
        Assert.False(ConfigFinder.NameMatches("other", MakeGame("Foo-Bar 2")));
    }

    [Fact]
    public void Find_RootWithManyFiles_StopsAtLimits()
    {
        WithTemp(dir =>
        {
            var install = Path.Combine(dir, "game");
            for (var i = 1; i <= 5; i++) Write(Path.Combine(install, $"f{i}.ini"), 40 * 1024);
            Write(Path.Combine(install, "big.ini"), 70 * 1024);

            var found = ConfigFinder.Find(MakeGame("Zed", install), Roots(dir));

            Assert.Equal(3, found.Count);
            Assert.True(found.Sum(c => c.Text.Length) <= ConfigFinder.MaxTotalBytes);
            Assert.True(found[^1].Truncated);
            Assert.DoesNotContain(found, c => c.Path.EndsWith("big.ini"));
        });
    }

    [Fact]
    public void Find_MyGamesAndSavedGames_FiltersByNameKeyword()
    {
        WithTemp(dir =>
        {
            var roots = Roots(dir);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "VideoSettings.ini"), 10);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "other.ini"), 10);
            Write(Path.Combine(roots.SavedGames, "zed", "graphics.cfg"), 10);

            var found = ConfigFinder.Find(MakeGame("Zed"), roots);

            Assert.Equal(2, found.Count);
        });
    }

    [Fact]
    public void Find_MasksCredentialLikeValues()
    {
        WithTemp(dir =>
        {
            var install = Path.Combine(dir, "game");
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "a.ini"), "Password=abc\r\nWidth=1920\r\nsession_token = xyz\r\nShadowPassQuality=3\r\n");

            var c = Assert.Single(ConfigFinder.Find(MakeGame("Zed", install), Roots(dir)));

            Assert.Contains("Password=***", c.Text);
            Assert.Contains("Width=1920", c.Text);
            Assert.Contains("session_token = ***\r\n", c.Text);
            Assert.Contains("ShadowPassQuality=3", c.Text);
            Assert.DoesNotContain("abc", c.Text);
            Assert.DoesNotContain("xyz", c.Text);
        });
    }

    [Fact]
    public void Find_SkipsEmuConfigsAndFilesOverOneMegabyte()
    {
        WithTemp(dir =>
        {
            var roots = Roots(dir);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "settings_huge.ini"), ConfigFinder.MaxFileBytes + 1);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "steam_emu_settings.ini"), 10);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "CREAM_API_config.ini"), 10);
            Write(Path.Combine(roots.Documents, "My Games", "Zed", "video.ini"), 10);

            var c = Assert.Single(ConfigFinder.Find(MakeGame("Zed"), roots));

            Assert.EndsWith("video.ini", c.Path);
        });
    }

    [Fact]
    public void Find_NoMatches_ReturnsEmpty()
    {
        WithTemp(dir => Assert.Empty(ConfigFinder.Find(MakeGame("Zed", Path.Combine(dir, "none")), Roots(dir))));
    }
}
