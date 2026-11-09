using System.IO.Compression;
using System.Text.Json;
using MetGameHUB.Update;

namespace MetGameHUB.Tests.Update;

public class UpdaterTests
{
    const string SelfContained = "metGameHUB-0.2.0-win-x64-selfcontained.zip";
    const string FrameworkDependent = "metGameHUB-0.2.0-win-x64.zip";

    static string Json(string tag, params string[] assetNames) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/Danilmet/metGameHUB/releases/tag/" + tag,
            assets = assetNames.Select(n => new { name = n, browser_download_url = "https://example.com/" + n }),
        });

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"assets\":{}}")]
    public void Parse_UnexpectedShape_ReturnsNull(string json) =>
        Assert.Null(Updater.Parse(json, new Version(0, 1, 0), selfContained: false));

    [Fact]
    public void Parse_Newer_ReturnsFrameworkDependentAsset()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained, FrameworkDependent), new Version(0, 1, 0, 0), selfContained: false);

        Assert.NotNull(r);
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.EndsWith(FrameworkDependent, r.AssetUrl);
        Assert.Equal("https://github.com/Danilmet/metGameHUB/releases/tag/v0.2.0", r.PageUrl);
    }

    [Fact]
    public void Parse_SelfContained_PicksSelfContainedAsset()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained, FrameworkDependent), new Version(0, 1, 0, 0), selfContained: true);

        Assert.NotNull(r);
        Assert.EndsWith(SelfContained, r.AssetUrl);
    }

    [Fact]
    public void Parse_SameVersion_ReturnsNull()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained, FrameworkDependent), new Version(0, 2, 0, 0), selfContained: false);

        Assert.Null(r);
    }

    [Fact]
    public void Parse_Older_ReturnsNull()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained, FrameworkDependent), new Version(0, 3, 0, 0), selfContained: false);

        Assert.Null(r);
    }

    [Fact]
    public void Parse_NoMatchingAsset_ReturnsNull()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained), new Version(0, 1, 0, 0), selfContained: false);

        Assert.Null(r);
    }

    [Fact]
    public void Parse_TagNotVersion_ReturnsNull()
    {
        var r = Updater.Parse(Json("latest", SelfContained, FrameworkDependent), new Version(0, 1, 0, 0), selfContained: false);

        Assert.Null(r);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        var r = Updater.Parse("{", new Version(0, 1, 0, 0), selfContained: false);

        Assert.Null(r);
    }

    [Fact]
    public void Extract_ZipWithExe_ReturnsExePath()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "metGameHUB.exe"), "new");
            var zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(staging, zip);

            var exe = Updater.Extract(zip, Path.Combine(root, "out"));

            Assert.True(File.Exists(exe));
            Assert.Equal("metGameHUB.exe", Path.GetFileName(exe));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_ZipWithoutExe_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "readme.txt"), "x");
            var zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(staging, zip);

            Assert.Throws<FileNotFoundException>(() => Updater.Extract(zip, Path.Combine(root, "out")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Swap_ReplacesExeAndKeepsOld()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(root);
            var exe = Path.Combine(root, "app.exe");
            var newExe = Path.Combine(root, "new.exe");
            File.WriteAllText(exe, "old");
            File.WriteAllText(newExe, "new");

            Updater.Swap(exe, newExe);

            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".old"));
            Assert.False(File.Exists(newExe));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Swap_MissingNew_RestoresOld()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(root);
            var exe = Path.Combine(root, "app.exe");
            File.WriteAllText(exe, "old");

            Assert.ThrowsAny<IOException>(() => Updater.Swap(exe, Path.Combine(root, "new.exe")));

            Assert.Equal("old", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".old"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_RemovesOldAndUpdateDir()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(root);
            var exe = Path.Combine(root, "app.exe");
            File.WriteAllText(exe + ".old", "old");
            var updateDir = Path.Combine(root, "update");
            Directory.CreateDirectory(updateDir);
            File.WriteAllText(Path.Combine(updateDir, "file.txt"), "x");

            Updater.Cleanup(exe, updateDir);

            Assert.False(File.Exists(exe + ".old"));
            Assert.False(Directory.Exists(updateDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_NothingToRemove_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(root);

            Updater.Cleanup(Path.Combine(root, "app.exe"), Path.Combine(root, "update"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
