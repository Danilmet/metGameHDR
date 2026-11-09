namespace MetGameHUB.Tests;

public class SettingsTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.True(s.TurnOffHdrOnExit);
        Assert.Empty(s.FolderRoots);
        Assert.Null(s.SteamGridDbKey);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndSetsFileAside()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "FolderRoots": ["C:\Games"] }""");
        try
        {
            var s = Settings.Load(path);
            Assert.Empty(s.FolderRoots);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            File.Delete(path + ".bad");
        }
    }

    [Fact]
    public void Save_Then_Load_Roundtrips()
    {
        var path = Path.GetTempFileName();
        try
        {
            var s = new Settings { FolderRoots = ["C:\\Games"], SteamGridDbKey = "k", Autostart = true };
            s.Save(path);
            var back = Settings.Load(path);
            Assert.Equal(["C:\\Games"], back.FolderRoots);
            Assert.Equal("k", back.SteamGridDbKey);
            Assert.True(back.Autostart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_Load_RoundTripsClaudeFields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            new Settings { ClaudeApiKey = "sk", ClaudeBaseUrl = "https://claude.nl.danmet.ru:8443/", ClaudeModel = "claude-sonnet-5-5", DefaultTarget = Advice.GoalStep.Smooth }.Save(path);
            Assert.Contains("\"DefaultTarget\": \"Smooth\"", File.ReadAllText(path));
            var back = Settings.Load(path);
            Assert.Equal("sk", back.ClaudeApiKey);
            Assert.Equal("https://claude.nl.danmet.ru:8443/", back.ClaudeBaseUrl);
            Assert.Equal("claude-sonnet-5-5", back.ClaudeModel);
            Assert.Equal(Advice.GoalStep.Smooth, back.DefaultTarget);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_OldDefaultFpsAndPriority_FallsBackToBalanced()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "ClaudeModel": "m", "DefaultFps": 90, "DefaultPriority": "Quality" }""");
        try
        {
            var s = Settings.Load(path);
            Assert.Equal(Advice.GoalStep.Balanced, s.DefaultTarget);
            Assert.Equal("m", s.ClaudeModel);
            Assert.False(File.Exists(path + ".bad"));
        }
        finally { File.Delete(path); File.Delete(path + ".bad"); }
    }
}
