namespace MetGameHDR.Tests;

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
            new Settings { ClaudeApiKey = "sk", DefaultFps = 90, DefaultPriority = Advice.Priority.Quality }.Save(path);
            Assert.Contains("\"DefaultPriority\": \"Quality\"", File.ReadAllText(path));
            var back = Settings.Load(path);
            Assert.Equal("sk", back.ClaudeApiKey);
            Assert.Equal(90, back.DefaultFps);
            Assert.Equal(Advice.Priority.Quality, back.DefaultPriority);
        }
        finally { File.Delete(path); }
    }
}
