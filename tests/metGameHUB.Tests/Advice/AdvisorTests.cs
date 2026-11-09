using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetGameHUB.Advice;
using MetGameHUB.Scan;
using Microsoft.Win32;
using Xunit;

namespace MetGameHUB.Tests;

public class AdvisorTests
{
    sealed class Fake(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Body = "";
        public Action? OnSend;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            OnSend?.Invoke();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
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
        public string BackupDir;
        string? regSub;

        public Env()
        {
            BackupDir = Path.Combine(Dir, "regbak");
            Directory.CreateDirectory(BackupDir);
            ConfigStore.BackupDir = () => BackupDir;
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

        public Advisor Advisor(string json = "{}", string? key = "key", HttpStatusCode status = HttpStatusCode.OK) => new(() => key, () => null, () => new ClaudeChoice(null), Lib, LibPath,
            () => new HardwareInfo("GPU", 8, "CPU", 16, 1920, 1080, 60, "Win"),
            Roots, _ => Running, new HttpClient(Handler = new Fake(json, status)));

        public Advice.Advice AdviceFor(string value = "2") =>
            new(new AdviceResult("s", [new SettingAdvice("Name", "1", "Rec", "S", "A", value, "r")], null), DateTime.UtcNow);

        public string RegKey()
        {
            regSub = @"Software\metGameHUB.Tests\" + Guid.NewGuid().ToString("N");
            using (var k = Registry.CurrentUser.CreateSubKey(regSub)) k.SetValue("Width_h1", 1920, RegistryValueKind.DWord);
            var key = @"HKCU\" + regSub;
            Lib.For(Game).ConfigPath = key;
            return key;
        }

        public object? RegValue(string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(regSub!);
            return k?.GetValue(name);
        }

        public void Dispose()
        {
            ConfigStore.BackupDir = () => AppPaths.RegistryDir;
            if (regSub != null) Registry.CurrentUser.DeleteSubKeyTree(regSub, false);
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    const string ReadJson = """{"settings":[{"name":"A","current":"1","section":"S","key":"A"}]}""";
    const string AskJson = """{"summary":"sum","settings":[{"name":"A","current":"1","recommended":"Rec","section":"S","key":"A","value":"9","reason":"r"}],"notes":null}""";

    static ConfigSnapshot Snap(Env e, string current = "1") =>
        new([new CurrentSetting("A", current, "S", "A")], DateTime.UtcNow, File.GetLastWriteTimeUtc(e.Ini));

    static JsonElement SentCurrent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        using var user = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
        return user.RootElement.GetProperty("current").Clone();
    }

    const string AdjustJson = """{"reply":"Поднял","summary":"new","settings":[{"name":"Shadows","current":"1","recommended":"High","section":"S","key":"A","value":"3","reason":"r"}],"notes":null}""";
    static ChatAttachment Png => new("a.png", new { type = "image" });

    static List<ChatTurn> Pair() => [new(ChatRole.User, "q", null, DateTime.UtcNow), new(ChatRole.Assistant, "a", null, DateTime.UtcNow)];

    [Fact]
    public async Task AdjustAsync_SavesAdviceAndBothTurns()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();

        var advice = await e.Advisor(Reply(AdjustJson)).AdjustAsync(e.Game, GoalStep.Balanced, "тени выше", [Png], CancellationToken.None);

        Assert.Equal("Shadows", advice.Result.Settings[0].Name);
        Assert.Equal("new", o.Advice!.Result.Summary);
        Assert.Collection(o.Chat!,
            t => { Assert.Equal(ChatRole.User, t.Role); Assert.Equal("тени выше", t.Text); Assert.Equal(["a.png"], t.Attachments); },
            t => { Assert.Equal(ChatRole.Assistant, t.Role); Assert.Equal("Поднял", t.Text); });
        var saved = Library.Load(e.LibPath).For(e.Game);
        Assert.Equal(2, saved.Chat!.Count);
        Assert.Equal("Shadows", saved.Advice!.Result.Settings[0].Name);
    }

    [Fact]
    public async Task AdjustAsync_EmptySettings_KeepsPreviousSettings()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();

        await e.Advisor(Reply("""{"reply":"Ок","summary":"s2","settings":[],"notes":"n2"}""")).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None);

        Assert.Equal("Name", o.Advice!.Result.Settings.Single().Name);
        Assert.Equal("s2", o.Advice.Result.Summary);
        Assert.Equal("n2", o.Advice.Result.Notes);
        Assert.Equal("Ок", o.Chat![1].Text);
        Assert.Null(o.Chat[0].Attachments);
    }

    [Fact]
    public async Task AdjustAsync_NullSettings_KeepsPreviousSettings()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();

        await e.Advisor(Reply("""{"reply":"Ок","summary":"s2","settings":null,"notes":null}""")).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None);

        Assert.Equal("Name", o.Advice!.Result.Settings.Single().Name);
    }

    [Fact]
    public async Task AdjustAsync_SecretKeyInAdvice_NotSent()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("Pw", "1", "R", "S", "password", "x", "r"), new SettingAdvice("Name", "1", "Rec", "S", "A", "2", "r")], null), DateTime.UtcNow);

        await e.Advisor(Reply(AdjustJson)).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None);

        using var doc = JsonDocument.Parse(e.Handler!.Body);
        using var ctx = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
        var sent = ctx.RootElement.GetProperty("advice").GetProperty("settings");
        Assert.Equal("Name", sent.EnumerateArray().Single().GetProperty("name").GetString());
    }

    [Fact]
    public async Task AdjustAsync_BlankReply_StoresPlaceholder()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();

        await e.Advisor(Reply("""{"reply":"","summary":"s","settings":[],"notes":null}""")).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None);

        Assert.Equal("(без комментария)", o.Chat![1].Text);
    }

    [Fact]
    public async Task AdjustAsync_ApiError_KeepsAdviceAndChat()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();
        o.Chat = Pair();
        var before = o.Advice;

        await Assert.ThrowsAsync<AdviceException>(() =>
            e.Advisor("{}", status: HttpStatusCode.InternalServerError).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None));

        Assert.Same(before, o.Advice);
        Assert.Equal(2, o.Chat.Count);
    }

    [Fact]
    public async Task AdjustAsync_NoKey_Throws()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Advice = e.AdviceFor();
        var advisor = e.Advisor(key: null);

        var ex = await Assert.ThrowsAsync<AdviceException>(() => advisor.AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None));

        Assert.Equal("Не задан ключ API", ex.Message);
        Assert.Equal("", e.Handler!.Body);
    }

    [Fact]
    public async Task AdjustAsync_NoAdvice_Throws()
    {
        using var e = new Env();

        var ex = await Assert.ThrowsAsync<AdviceException>(() => e.Advisor().AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None));

        Assert.Equal("Сначала подберите настройки", ex.Message);
    }

    [Fact]
    public async Task AdjustAsync_BlankMessage_ThrowsWithoutRequest()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Advice = e.AdviceFor();
        var advisor = e.Advisor();

        var ex = await Assert.ThrowsAsync<AdviceException>(() => advisor.AdjustAsync(e.Game, GoalStep.Balanced, "  ", [], CancellationToken.None));

        Assert.Equal("Пустое сообщение", ex.Message);
        Assert.Equal("", e.Handler!.Body);
    }

    [Fact]
    public async Task AdjustAsync_SendsHistoryAndFreshCurrent()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Advice = e.AdviceFor();
        o.Chat = Pair();
        o.Snapshot = Snap(e);

        await e.Advisor(Reply(AdjustJson)).AdjustAsync(e.Game, GoalStep.Balanced, "x", [], CancellationToken.None);

        using var doc = JsonDocument.Parse(e.Handler!.Body);
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        var context = msgs[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"current\"", context);
        Assert.Contains("\"advice\"", context);
        Assert.Equal(4, o.Chat!.Count);
    }

    [Fact]
    public async Task AskAsync_ClearsChat()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Chat = Pair();

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        Assert.Null(o.Chat);
    }

    [Fact]
    public void Restore_ClearsChat()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini + ".orig", "[S]\r\nA=0\r\n");
        var o = e.Lib.For(e.Game);
        o.Chat = Pair();

        e.Advisor().Restore(e.Game);

        Assert.Null(o.Chat);
    }

    [Fact]
    public async Task LocateAsync_NewPath_ClearsChat_SamePath_Keeps()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.Chat = Pair();

        await e.Advisor(Locate(e.Ini, "ini")).LocateAsync(e.Game, CancellationToken.None);
        Assert.NotNull(o.Chat);

        await e.Advisor(Locate(@"<InstallDir>\other.ini", "ini")).LocateAsync(e.Game, CancellationToken.None);
        Assert.Null(o.Chat);
    }

    [Fact]
    public async Task ReadAsync_StoresSnapshotWithStamp()
    {
        using var e = new Env();

        var snap = await e.Advisor(Reply(ReadJson)).ReadAsync(e.Game, CancellationToken.None);

        Assert.Equal("A", snap!.Settings[0].Name);
        Assert.Equal(File.GetLastWriteTimeUtc(e.Ini), snap.FileStamp);
        Assert.Equal(File.GetLastWriteTimeUtc(e.Ini), Library.Load(e.LibPath).For(e.Game).Snapshot!.FileStamp);
    }

    [Fact]
    public async Task ReadAsync_NoFile_ReturnsNullWithoutRequest()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = Path.Combine(e.Install, "none.ini");

        var snap = await e.Advisor(Reply(ReadJson)).ReadAsync(e.Game, CancellationToken.None);

        Assert.Null(snap);
        Assert.Equal("", e.Handler!.Body);
    }

    [Fact]
    public async Task ReadAsync_SendsMaskedText()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "[S]\r\nA=1\r\nPassword=hunter2\r\n");

        await e.Advisor(Reply(ReadJson)).ReadAsync(e.Game, CancellationToken.None);

        Assert.Contains("Password=***", e.Handler!.Body);
        Assert.DoesNotContain("hunter2", e.Handler.Body);
    }

    [Fact]
    public void IsSnapshotFresh_ChangedStamp_False()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);
        Assert.True(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));

        File.SetLastWriteTimeUtc(e.Ini, DateTime.UtcNow.AddMinutes(1));

        Assert.False(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public void IsSnapshotFresh_MissingFile_False()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);
        File.Delete(e.Ini);

        Assert.False(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public void Snapshot_RoundTrip_StaysFresh()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);
        e.Lib.Save(e.LibPath);

        Assert.True(Advisor.IsSnapshotFresh(Library.Load(e.LibPath), e.Roots, e.Game));
    }

    [Fact]
    public async Task Locate_OtherPath_ResetsSnapshot()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);

        await e.Advisor(Locate(@"<InstallDir>\other.ini", "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.Null(e.Lib.For(e.Game).Snapshot);
    }

    [Fact]
    public async Task Locate_SamePath_KeepsSnapshot()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);

        await e.Advisor(Locate(e.Ini, "ini")).LocateAsync(e.Game, CancellationToken.None);

        Assert.NotNull(e.Lib.For(e.Game).Snapshot);
    }

    [Fact]
    public async Task AskAsync_SendsCurrentOnlyWhenFresh()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e);
        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);
        Assert.Equal("A", SentCurrent(e.Handler!.Body)[0].GetProperty("name").GetString());

        e.Lib.For(e.Game).Snapshot = Snap(e);
        File.SetLastWriteTimeUtc(e.Ini, DateTime.UtcNow.AddMinutes(1));
        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);
        Assert.Equal(JsonValueKind.Null, SentCurrent(e.Handler!.Body).ValueKind);
    }

    [Fact]
    public async Task AskAsync_UpdatesSnapshotFromAnswer()
    {
        using var e = new Env();

        await e.Advisor(Reply(AskJson)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        var snap = e.Lib.For(e.Game).Snapshot!;
        Assert.Equal("1", snap.Settings[0].Current);
        Assert.True(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public void Apply_And_Restore_KeepSnapshotFresh()
    {
        using var e = new Env();
        var advice = e.AdviceFor();
        e.Lib.For(e.Game).Advice = advice;
        File.SetLastWriteTimeUtc(e.Ini, DateTime.UtcNow.AddHours(-1));
        e.Lib.For(e.Game).Snapshot = Snap(e);

        e.Advisor().Apply(e.Game, advice);
        Assert.Equal("Rec", e.Lib.For(e.Game).Snapshot!.Settings[0].Current);
        Assert.True(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));

        e.Advisor().Restore(e.Game);
        Assert.Equal("1", e.Lib.For(e.Game).Snapshot!.Settings[0].Current);
        Assert.True(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public void Restore_KeyAddedByApply_CurrentBecomesNull()
    {
        using var e = new Env();
        var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("B", null, "Rec", "S", "B", "2", "r")], null), DateTime.UtcNow);
        e.Lib.For(e.Game).Advice = advice;
        e.Lib.For(e.Game).Snapshot = new([new CurrentSetting("B", "old", "S", "B")], DateTime.UtcNow, File.GetLastWriteTimeUtc(e.Ini));

        e.Advisor().Apply(e.Game, advice);
        e.Advisor().Restore(e.Game);

        Assert.Null(e.Lib.For(e.Game).Snapshot!.Settings[0].Current);
    }

    [Fact]
    public async Task ReadAsync_FileChangedDuringRequest_Stale()
    {
        using var e = new Env();
        var advisor = e.Advisor(Reply(ReadJson));
        e.Handler!.OnSend = () => File.SetLastWriteTimeUtc(e.Ini, DateTime.UtcNow.AddHours(1));

        await advisor.ReadAsync(e.Game, CancellationToken.None);

        Assert.False(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public void Apply_StaleSnapshot_StaysStale()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = Snap(e) with { FileStamp = DateTime.UtcNow.AddDays(-2) };

        e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal("Rec", e.Lib.For(e.Game).Snapshot!.Settings[0].Current);
        Assert.False(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public async Task AskAsync_NoKeyedRows_KeepsSnapshot()
    {
        using var e = new Env();
        var before = Snap(e, "keep");
        e.Lib.For(e.Game).Snapshot = before;

        await e.Advisor(Reply("""{"summary":"s","settings":[{"name":"M","current":null,"recommended":"R","section":null,"key":null,"value":null,"reason":"r"}],"notes":null}"""))
            .AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        Assert.Same(before, e.Lib.For(e.Game).Snapshot);
    }

    [Fact]
    public async Task AskAsync_FreshSnapshot_MergesRows()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = new([new CurrentSetting("A", "0", "S", "A"), new CurrentSetting("Other", "x", "S", "Other")], DateTime.UtcNow, File.GetLastWriteTimeUtc(e.Ini));

        await e.Advisor(Reply(AskJson)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        var rows = e.Lib.For(e.Game).Snapshot!.Settings;
        Assert.Equal("1", rows.Single(r => r.Key == "A").Current);
        Assert.Contains(rows, r => r.Key == "Other" && r.Current == "x");
        Assert.True(Advisor.IsSnapshotFresh(e.Lib, e.Roots, e.Game));
    }

    [Fact]
    public async Task AskAsync_CurrentExcludesSecretKeys()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = new([new CurrentSetting("A", "1", "S", "A"), new CurrentSetting("Pw", "hunter2", "S", "UserPassword")], DateTime.UtcNow, File.GetLastWriteTimeUtc(e.Ini));

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        Assert.Single(SentCurrent(e.Handler!.Body).EnumerateArray());
        Assert.DoesNotContain("hunter2", e.Handler.Body);
    }

    [Fact]
    public void Apply_SnapshotKeyDifferentCase_UpdatesCurrent()
    {
        using var e = new Env();
        e.Lib.For(e.Game).Snapshot = new([new CurrentSetting("A", "1", "s", "a")], DateTime.UtcNow, File.GetLastWriteTimeUtc(e.Ini));

        e.Advisor().Apply(e.Game, e.AdviceFor());

        Assert.Equal("Rec", e.Lib.For(e.Game).Snapshot!.Settings[0].Current);
    }

    [Fact]
    public async Task AskAsync_StoresAdviceOnOverrideAndSaves()
    {
        using var e = new Env();
        var goal = GoalStep.Pretty;

        var advice = await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, goal, CancellationToken.None);

        Assert.Equal("sum", advice.Result.Summary);
        Assert.Same(advice, e.Lib.For(e.Game).Advice);
        Assert.Null(e.Lib.For(e.Game).Target);
        Assert.Equal("sum", Library.Load(e.LibPath).For(e.Game).Advice!.Result.Summary);
    }

    [Fact]
    public async Task AskAsync_WithoutConfig_SendsNullConfig()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = null;

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

        Assert.Equal(JsonValueKind.Null, SentConfig(e.Handler!.Body).ValueKind);
    }

    [Fact]
    public async Task AskAsync_SendsMaskedConfigText()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "[S]\r\nA=1\r\nPassword=hunter2\r\n");

        await e.Advisor(Reply(EmptyAdvice)).AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None);

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
        await Assert.ThrowsAnyAsync<Exception>(() => advisor.AskAsync(e.Game, GoalStep.Balanced, CancellationToken.None));

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
    public void Apply_UpdatesStoredCurrentValues()
    {
        using var e = new Env();
        var advice = e.AdviceFor();
        e.Lib.For(e.Game).Advice = advice;

        Assert.Equal(ApplyOutcome.Applied, e.Advisor().Apply(e.Game, advice));

        Assert.Equal("Rec", Library.Load(e.LibPath).For(e.Game).Advice!.Result.Settings[0].Current);
    }

    [Fact]
    public void Restore_PutsOrigBack_KeepsBak_UpdatesCurrent()
    {
        using var e = new Env();
        var advice = e.AdviceFor();
        e.Lib.For(e.Game).Advice = advice;
        e.Advisor().Apply(e.Game, advice);

        Assert.Equal(ApplyOutcome.Applied, e.Advisor().Restore(e.Game));

        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini));
        Assert.Equal("[S]\r\nA=2\r\n", File.ReadAllText(e.Ini + ".bak"));
        Assert.True(File.Exists(e.Ini + ".orig"));
        Assert.Equal("1", Library.Load(e.LibPath).For(e.Game).Advice!.Result.Settings[0].Current);
    }

    [Fact]
    public void Restore_NoOrigOrGameRunning_LeavesFile()
    {
        using var e = new Env();
        Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Restore(e.Game));

        e.Advisor().Apply(e.Game, e.AdviceFor());
        e.Running = true;
        Assert.Equal(ApplyOutcome.GameRunning, e.Advisor().Restore(e.Game));
        Assert.Equal("[S]\r\nA=2\r\n", File.ReadAllText(e.Ini));
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

    [Fact] public async Task LocateAsync_Registry_AllowedKeyStored()
    {
        using var e = new Env();
        var status = await e.Advisor(Locate(@"HKEY_CURRENT_USER\Software\Co\Game\", "registry")).LocateAsync(e.Game, default);
        Assert.Equal(@"HKCU\Software\Co\Game", status.Path);
        Assert.Equal("registry", status.Format);
        Assert.False(status.Exists);
        Assert.Equal(@"HKCU\Software\Co\Game", e.Lib.For(e.Game).ConfigPath);
    }

    [Fact] public async Task LocateAsync_Registry_MicrosoftKey_Rejected()
    {
        using var e = new Env();
        var status = await e.Advisor(Locate(@"HKCU\Software\Microsoft\Windows", "registry")).LocateAsync(e.Game, default);
        Assert.Null(status.Path);
        Assert.Null(e.Lib.For(e.Game).ConfigPath);
        Assert.Equal("Модель указала путь вне разрешённых папок", e.Lib.For(e.Game).ConfigNote);
    }

    [Fact] public async Task AskAsync_RegistryKey_SendsDumpAsRegistry()
    {
        using var e = new Env(); e.RegKey();
        await e.Advisor(Reply(AskJson)).AskAsync(e.Game, GoalStep.Balanced, default);
        var cfg = SentConfig(e.Handler!.Body);
        Assert.Equal("registry", cfg.GetProperty("format").GetString());
        Assert.Equal("Width_h1=1920", cfg.GetProperty("text").GetString());
    }

    [Fact] public void Apply_RegistryKey_WritesAndBacksUp_ThenRestore()
    {
        using var e = new Env(); e.RegKey();
        var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", "1920", "2560", null, "Width_h1", "2560", "r")], null), DateTime.UtcNow);
        Assert.Equal(ApplyOutcome.Applied, e.Advisor().Apply(e.Game, advice));
        Assert.Equal(2560, e.RegValue("Width_h1"));
        Assert.Single(Directory.GetFiles(e.BackupDir, "*.orig.reg"));
        Assert.Single(Directory.GetFiles(e.BackupDir, "*.bak.reg"));
        Assert.Equal("2560", e.Lib.For(e.Game).Advice!.Result.Settings[0].Current);
        Assert.Equal(ApplyOutcome.Applied, e.Advisor().Restore(e.Game));
        Assert.Equal(1920, e.RegValue("Width_h1"));
        Assert.Equal("1920", e.Lib.For(e.Game).Advice!.Result.Settings[0].Current);
    }

    [Fact] public void Apply_RegistryKeyMissing_FileMissing()
    {
        using var e = new Env();
        e.Lib.For(e.Game).ConfigPath = @"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N");
        Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Apply(e.Game, new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", null, "1", null, "Width_h1", "1", "r")], null), DateTime.UtcNow)));
    }

    [Fact] public void Apply_RegistryBadName_BadEdit()
    {
        using var e = new Env(); e.RegKey();
        var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", null, "1", null, "Nope_h9", "1", "r")], null), DateTime.UtcNow);
        Assert.Equal(ApplyOutcome.BadEdit, e.Advisor().Apply(e.Game, advice));
    }
}
