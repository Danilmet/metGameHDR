using System.Text.Json;
using MetGameHDR.Update;

namespace MetGameHDR.Tests.Update;

public class UpdaterTests
{
    const string SelfContained = "metGameHDR-0.2.0-win-x64-selfcontained.zip";
    const string FrameworkDependent = "metGameHDR-0.2.0-win-x64.zip";

    static string Json(string tag, params string[] assetNames) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/Danilmet/metGameHDR/releases/tag/" + tag,
            assets = assetNames.Select(n => new { name = n, browser_download_url = "https://example.com/" + n }),
        });

    [Fact]
    public void Parse_Newer_ReturnsFrameworkDependentAsset()
    {
        var r = Updater.Parse(Json("v0.2.0", SelfContained, FrameworkDependent), new Version(0, 1, 0, 0), selfContained: false);

        Assert.NotNull(r);
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.EndsWith(FrameworkDependent, r.AssetUrl);
        Assert.Equal("https://github.com/Danilmet/metGameHDR/releases/tag/v0.2.0", r.PageUrl);
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
}
