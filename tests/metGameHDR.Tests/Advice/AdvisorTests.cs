using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetGameHDR.Advice;
using MetGameHDR.Scan;
using Xunit;

namespace MetGameHDR.Tests;

public class AdvisorTests
{
    sealed class Fake(string json) : HttpMessageHandler
    {
        public string Body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    static string Reply(string text) => JsonSerializer.Serialize(new
    {
        stop_reason = "end_turn",
        content = new object[] { new { type = "text", text } },
    });

    const string EmptyAdvice = """{"summary":"sum","settings":[],"notes":null}""";

    static string Locate(string? path, string format) =>
        Reply(JsonSerializer.Serialize(new { path, format, note = "n" }));

    // Config text the model was sent, as seen in the request body.
    static JsonElement SentConfig(string body)
    {
        using var doc = JsonDocument.Parse(body);
        using var user = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
        return user.RootElement.GetProperty("config").Clone();
    }

    sealed class Env : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Install, Ini, LibPath;
        public Game Game;
        public Library Lib = new();
        public bool Running;
        public Fake? Handler;

        public Env()
        {
            Install = Path.Combine(Dir, "game");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Path.Combine(Dir, "empty"));
            Ini = Path.Combine(Install, "a.ini");
            File.WriteAllText(Ini, "[S]\r\nA=1\r\n");
            LibPath = Path.Combine(Dir, "lib.json");
            Game = new Game("Game", Install, GameSource.Folder, "g.exe");
            Lib.For(Game).ConfigPath = Ini;
        }

        public FinderRoots Roots => new(Path.Combine(Dir, "empty"), Path.Combine(Dir, "empty"), Path.Combine(Dir, "empty"));

        public Advisor Advisor(string json = "{}") => new(() => "key", () => null, () => null, Lib, LibPath,
            () => new HardwareInfo("GPU", 8, "CPU", 16, 1920, 1080, 60, "Win"),
            Roots, _ => Running, new HttpClient(Handler = new Fake(json)));

        public Advice.Advice AdviceFor(string value = "2") =>
            new(new AdviceResult("s", [new SettingAdvice("Name", "1", "Rec", "S", "A", value, "r")], null), DateTime.UtcNow);

        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task AskAsync_StoresAdviceOnOverrideAndSaves()
    {
        using var e = new Env();
        var goal = new GraphicsGoal(60, Priority.Quality);

        var advice = await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, goal, CancellationToken.None);

        Assert.Equal("sum", advice.Result.Summary);
        Assert.Same(advice, e.Lib.For(e.Game).Advice);
        Assert.Equal(goal, e.Lib.For(e.Game).Goal);
        Assert.Equal("sum", Library.Load(e.LibPath).For(e.Game).Advice!.Result.Summary);
    }

    [Fact]
    public async Task AskAsync_WithoutConfig_SendsNullConfig()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = null;

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, new GraphicsGoal(60, Priority.Balanced), CancellationToken.None);

        Assert.Equal(JsonValueKind.Null, SentConfig(e.Handler!.Body).ValueKind);
    }

    [Fact]
    public async Task AskAsync_SendsMaskedConfigText()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "[S]\r\nA=1\r\nPassword=hunter2\r\n");

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, new GraphicsGoal(60, Priority.Balanced), CancellationToken.None);

        var config = SentConfig(e.Handler!.Body);
        Assert.Equal(e.Ini, config.GetProperty("path").GetString());
        Assert.Equal("ini", config.GetProperty("format").GetString());
        var text = config.GetProperty("text").GetString()!;
        Assert.Contains("A=1", text);
        Assert.Contains("Password=***", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.False(config.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AskAsync_UsesOverrideNameAndExeInRequest()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.SearchName = "Real Name";
        o.MainExe = "real.exe";

        var advisor = e.Advisor();
        await Assert.ThrowsAnyAsync<Exception>(() => advisor.AskAsync(e.Game, new GraphicsGoal(60, Priority.Balanced), CancellationToken.None));

        Assert.Contains("Real Name", e.Handler!.Body);
        Assert.Contains("real.exe", e.Handler.Body);
        Assert.DoesNotContain("g.exe", e.Handler.Body);
    }

    [Fact]
    public async Task LocateAsync_ResolvesEnvVarsAndStoresPath()
    {
        using var e = new Env();
        var name = "mgh-" + Guid.NewGuid().ToString("N");
        var json = Locate($@"%LocalAppData%\{name}\a.ini", "ini");

        var status = await e.Advisor(json).LocateAsync(e.Game, CancellationToken.None);

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name, "a.ini");
        Assert.Equal(new ConfigStatus(expected, false, "ini", "n"), status);
        Assert.Equal(expected, Library.Load(e.LibPath).For(e.Game).ConfigPath);
        Assert.Contains("game", e.Handler!.Body);
    }

    [Fact]
    public async Task LocateAsync_FileMissing_ReportsExistsFalse()
    {
        using var e = new Env();
        var missing = Path.Combine(e.Install, "later.ini");

        var status = await e.Advisor(Locate(@"<InstallDir>\later.ini", "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Equal(new ConfigStatus(missing, false, "ini", "n"), status);
        Assert.Equal(missing, e.Lib.For(e.Game).ConfigPath);
    }

    [Fact]
    public async Task LocateAsync_ExistingFile_ReportsExistsTrue()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = null;

        var status = await e.Advisor(Locate(e.Ini, "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.True(status.Exists);
        Assert.Equal(e.Ini, e.Lib.For(e.Game).ConfigPath);
    }

    [Fact]
    public async Task LocateAsync_NoneFormat_ClearsPath()
    {
        using var e = new Env();

        var status = await e.Advisor(Locate(null, "none")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Equal(new ConfigStatus(null, false, "none", "n"), status);
        Assert.Null(e.Lib.For(e.Game).ConfigPath);
        Assert.Null(Library.Load(e.LibPath).For(e.Game).ConfigPath);
    }

    [Fact]
    public async Task LocateAsync_SendsInstallRootListing()
    {
        using var e = new Env();
        Directory.CreateDirectory(Path.Combine(e.Install, "Sub"));
        Directory.CreateDirectory(Path.Combine(e.Install, "ProjectX", "Binaries"));
        Directory.CreateDirectory(Path.Combine(e.Install, "ProjectX", "Content"));
        File.WriteAllText(Path.Combine(e.Install, "b.txt"), "");

        await e.Advisor(Locate(null, "none")).LocateAsync(e.Game, CancellationToken.None);

        using var doc = JsonDocument.Parse(e.Handler!.Body);
        using var user = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
        var sent = user.RootElement;
        Assert.Equal(["a.ini", "b.txt", "ProjectX\\", "Sub\\"], sent.GetProperty("installRootEntries").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("ProjectX", sent.GetProperty("unrealProject").GetString());
    }

    [Fact]
    public async Task LocateAsync_StoresNote_EvenWithoutPath()
    {
        using var e = new Env();

        await e.Advisor(Locate(null, "none")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Equal("n", Library.Load(e.LibPath).For(e.Game).ConfigNote);
    }

    [Fact]
    public async Task LocateAsync_PathOutsideAllowedRoots_TreatedAsNone()
    {
        using var e = new Env();

        var status = await e.Advisor(Locate(@"C:\Windows\System32\drivers\etc\hosts", "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Equal(new ConfigStatus(null, false, "none", "Модель указала путь вне разрешённых папок"), status);
        Assert.Null(e.Lib.For(e.Game).ConfigPath);
    }

    [Theory]
    [InlineData(@"<InstallDir>\..\..\..\..\..\..\..\..\..\..\..\..\x.ini")]
    [InlineData(@"relative\a.ini")]
    public async Task LocateAsync_EscapingOrRelativePath_TreatedAsNone(string path)
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = null;

        var status = await e.Advisor(Locate(path, "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Equal("none", status.Format);
        Assert.Null(e.Lib.For(e.Game).ConfigPath);
    }

    [Fact]
    public void ResolvePath_ExpandsInstallDirPlaceholderAndNormalizes() =>
        Assert.Equal(@"C:\g\Saved\a.ini", Advisor.ResolvePath(@"<installdir>\x\..\Saved\a.ini", @"C:\g"));

    [Fact]
    public void IsAllowedPath_InstallDirAndHome_TrueOthersFalse()
    {
        var roots = FinderRoots.Default();
        Assert.True(Advisor.IsAllowedPath(@"D:\Games\g\a.ini", @"D:\Games\g", roots));
        Assert.False(Advisor.IsAllowedPath(@"D:\Games\g2\a.ini", @"D:\Games\g", roots));
        Assert.False(Advisor.IsAllowedPath(@"D:\Games\g", @"D:\Games\g", roots));
        Assert.True(Advisor.IsAllowedPath(Path.Combine(roots.LocalAppData, "g", "a.ini"), @"D:\Games\g", roots));
        Assert.False(Advisor.IsAllowedPath(@"C:\Windows\win.ini", @"D:\Games\g", roots));
    }

    [Fact]
    public void Edits_OnlySettingsWithKey()
    {
        var result = new AdviceResult("s",
        [
            new SettingAdvice("A", "1", "2", "S", "K1", "5", "r"),
            new SettingAdvice("B", null, "High", null, null, null, "r"),
            new SettingAdvice("C", null, "On", null, "K2", null, "r"),
        ], null);

        var edits = Advisor.Edits(result);

        Assert.Equal([new ConfigEdit("S", "K1", "5"), new ConfigEdit(null, "K2", "")], edits);
    }

    [Fact]
    public void Apply_NoEdits_ReturnsNoConfig()
    {
        using var e = new Env();
        var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("B", null, "High", null, null, null, "r")], null), DateTime.UtcNow);

        Assert.Equal(ApplyOutcome.NoConfig, e.Advisor().Apply(e.Game, advice));
    }

    [Fact]
    public void Apply_NoConfigPathOrFileMissing_ReturnsFileMissing()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = null;
        Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Apply(e.Game, e.AdviceFor()));

        e.Lib.For(e.Game).ConfigPath = Path.Combine(e.Install, "none.ini");
        Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Apply(e.Game, e.AdviceFor()));
    }

    [Fact]
    public void Apply_GameRunning_ReturnsGameRunning_AndFileUntouched()
    {
        using var e = new Env { Running = true };

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal(ApplyOutcome.GameRunning, outcome);
        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini));
        Assert.False(File.Exists(e.Ini + ".orig"));
    }

    [Fact]
    public void Apply_PathWithDifferentCaseAndSlashes_StillApplies()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = e.Ini.ToUpperInvariant().Replace('\\', '/');

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Contains("A=2", File.ReadAllText(e.Ini));
    }

    [Fact]
    public void Apply_StoredPathOutsideAllowedRoots_ReturnsFileMissing()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = @"C:\Windows\win.ini";

        Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Apply(e.Game, e.AdviceFor()));
    }

    [Fact]
    public void Apply_Twice_KeepsFirstOrigAndRewritesBak()
    {
        using var e = new Env();
        var advisor = e.Advisor();
        advisor.Apply(e.Game, e.AdviceFor());

        advisor.Apply(e.Game, e.AdviceFor("3"));

        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini + ".orig"));
        Assert.Contains("A=2", File.ReadAllText(e.Ini + ".bak"));
        Assert.Contains("A=3", File.ReadAllText(e.Ini));
    }

    [Fact]
    public void Apply_JsonFile_MissingKey_ReturnsNotIni()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "  {\"a\":1}");

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal(ApplyOutcome.NotIni, outcome);
        Assert.Equal("  {\"a\":1}", File.ReadAllText(e.Ini));
        Assert.False(File.Exists(e.Ini + ".orig"));
    }

    [Fact]
    public void Apply_LineJsonWithKeys_Applies()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "{\n\t\"A\": 1,\n\t\"B\": 2\n}\n");

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("{\n\t\"A\": 2,\n\t\"B\": 2\n}\n", File.ReadAllText(e.Ini));
        Assert.True(File.Exists(e.Ini + ".orig"));
        Assert.True(File.Exists(e.Ini + ".bak"));
    }

    [Fact]
    public void CanWrite_Xml_IsFalse() =>
        Assert.False(Advisor.CanWrite("<?xml version=\"1.0\"?><a/>", [new ConfigEdit(null, "a", "1")]));

    [Theory]
    [InlineData("A", "2\r\nEvil=1")]
    [InlineData("", "2")]
    [InlineData("A=B", "2")]
    [InlineData("A\n", "2")]
    public void Apply_BadEdit_ReturnsBadEdit(string key, string value)
    {
        using var e = new Env();
        var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("N", null, "R", "S", key, value, "r")], null), DateTime.UtcNow);

        var outcome = e.Advisor().Apply(e.Game, advice);

        Assert.Equal(ApplyOutcome.BadEdit, outcome);
        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini));
    }
}
