using MetGameHDR.Steam;

namespace MetGameHDR.Tests.Steam;

public class SteamLocatorTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public SteamLocatorTests() => Directory.CreateDirectory(Path.Combine(root, "config"));

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void MostRecentUserId_PicksMaxTimestamp()
    {
        File.WriteAllText(Path.Combine(root, @"config\loginusers.vdf"),
            "\"users\"\n{\n\t\"76561198091448058\"\n\t{\n\t\t\"AccountName\"\t\"a\"\n\t\t\"timestamp\"\t\"200\"\n\t}\n\t\"76561198073896558\"\n\t{\n\t\t\"AccountName\"\t\"b\"\n\t\t\"timestamp\"\t\"100\"\n\t}\n}\n");
        Assert.Equal("131182330", SteamLocator.MostRecentUserId(root));
    }

    [Fact]
    public void MostRecentUserId_MissingFileOrNoUsers_ReturnsNull()
    {
        Assert.Null(SteamLocator.MostRecentUserId(root));
        File.WriteAllText(Path.Combine(root, @"config\loginusers.vdf"), "\"users\" { }");
        Assert.Null(SteamLocator.MostRecentUserId(root));
    }

    [Fact]
    public void UserConfigDir_CombinesUserdataPath() =>
        Assert.Equal(@"C:\S\userdata\5\config", SteamLocator.UserConfigDir(@"C:\S", "5"));
}
