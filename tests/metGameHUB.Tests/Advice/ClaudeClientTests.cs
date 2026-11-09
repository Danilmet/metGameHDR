using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetGameHUB.Advice;
using MetGameHUB.Scan;
using Xunit;

namespace MetGameHUB.Tests;

public class ClaudeClientTests
{
    sealed class Fake(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        }
    }

    const string ResultJson = """
        {"summary":"s","settings":[
          {"name":"Shadows","current":"High","recommended":"Low","section":"S","key":"sg.ShadowQuality","value":"0","reason":"r"},
          {"name":"DLSS","current":null,"recommended":"Quality","section":null,"key":null,"value":null,"reason":"r"}],
         "notes":null}
        """;

    const string LocationJson = """{"path":"%LocalAppData%\\g\\a.ini","format":"ini","note":"n"}""";

    static string Response(string stopReason = "end_turn", string text = ResultJson) => JsonSerializer.Serialize(new
    {
        stop_reason = stopReason,
        content = new object[]
        {
            new { type = "server_tool_use", name = "web_search" },
            new { type = "web_search_tool_result", content = "x" },
            new { type = "text", text },
        },
    });

    static AdviceRequest Request() => new(
        new Game("G", @"C:\g", GameSource.Steam, "g.exe"),
        new HardwareInfo("RTX", 8, "CPU", 16, 2560, 1440, 144, "Win11"),
        GoalStep.Balanced,
        @"C:\g\a.ini", "ini", "k=1", false);

    static JsonElement UserJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!).RootElement.Clone();
    }

    [Fact]
    public void BuildAskBody_GoalHasModeAndFps()
    {
        var user = UserJson(ClaudeClient.BuildAskBody(Request(), "m"));
        Assert.Equal("balanced", user.GetProperty("goal").GetProperty("mode").GetString());
        Assert.Equal(100, user.GetProperty("goal").GetProperty("fps").GetInt32());
        var max = UserJson(ClaudeClient.BuildAskBody(Request() with { Goal = GoalStep.MaxFps }, "m"));
        Assert.Equal(JsonValueKind.Null, max.GetProperty("goal").GetProperty("fps").ValueKind);
    }

    static LocateRequest LocateReq() => new(
        new Game("G", @"C:\g", GameSource.Steam, "g.exe"), ["Binaries\\", "g.exe"], [@"C:\g\a.ini"], "ProjectX");

    [Fact]
    public async Task LocateAsync_SendsGameListingAndCandidates_ParsesLocation()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: LocationJson));
        var loc = await new ClaudeClient(new HttpClient(fake), "key").LocateAsync(LocateReq(), CancellationToken.None);

        Assert.Equal(new ConfigLocation(@"%LocalAppData%\g\a.ini", "ini", "n"), loc);
        using var doc = JsonDocument.Parse(fake.Body!);
        var root = doc.RootElement;
        Assert.Equal(1000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(2, root.GetProperty("tools")[0].GetProperty("max_uses").GetInt32());
        Assert.Contains("ОДИН файл", root.GetProperty("system").GetString());
        using var user = JsonDocument.Parse(root.GetProperty("messages")[0].GetProperty("content").GetString()!);
        var u = user.RootElement;
        Assert.Equal("G", u.GetProperty("game").GetProperty("name").GetString());
        Assert.Equal(@"C:\g", u.GetProperty("game").GetProperty("installDir").GetString());
        Assert.Equal("g.exe", u.GetProperty("game").GetProperty("exe").GetString());
        Assert.Equal("Binaries\\", u.GetProperty("installRootEntries")[0].GetString());
        Assert.Equal(@"C:\g\a.ini", u.GetProperty("candidates")[0].GetString());
        Assert.Equal("ProjectX", u.GetProperty("unrealProject").GetString());
    }

    [Fact]
    public async Task LocateAsync_NoneFormat_PathNull()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: """{"path":null,"format":"none","note":"реестр"}"""));
        var loc = await new ClaudeClient(new HttpClient(fake), "key").LocateAsync(LocateReq(), CancellationToken.None);

        Assert.Null(loc.Path);
        Assert.Equal("none", loc.Format);
    }

    [Fact]
    public async Task AskAsync_BodyHasConfigObjectOrNull()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        var client = new ClaudeClient(new HttpClient(fake), "key");

        await client.AskAsync(Request(), CancellationToken.None);
        using (var doc = JsonDocument.Parse(fake.Body!))
        {
            Assert.Equal(8000, doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("tools")[0].GetProperty("max_uses").GetInt32());
            using var user = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            var config = user.RootElement.GetProperty("config");
            Assert.Equal(@"C:\g\a.ini", config.GetProperty("path").GetString());
            Assert.Equal("ini", config.GetProperty("format").GetString());
            Assert.Equal("k=1", config.GetProperty("text").GetString());
            Assert.False(config.GetProperty("truncated").GetBoolean());
            Assert.Equal("balanced", user.RootElement.GetProperty("goal").GetProperty("mode").GetString());
        }

        await client.AskAsync(Request() with { ConfigPath = null, ConfigFormat = null, ConfigText = null }, CancellationToken.None);
        using var doc2 = JsonDocument.Parse(fake.Body!);
        using var user2 = JsonDocument.Parse(doc2.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
        Assert.Equal(JsonValueKind.Null, user2.RootElement.GetProperty("config").ValueKind);
    }

    [Fact]
    public void Parse_SettingsWithKeys_MapsCurrentAndKey()
    {
        var result = ClaudeClient.Parse<AdviceResult>(Response());

        Assert.Equal(new SettingAdvice("Shadows", "High", "Low", "S", "sg.ShadowQuality", "0", "r"), result.Settings[0]);
        Assert.Equal(new SettingAdvice("DLSS", null, "Quality", null, null, null, "r"), result.Settings[1]);
    }

    [Fact]
    public async Task AskAsync_SendsHeadersModelAndTools()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        await new ClaudeClient(new HttpClient(fake), "key").AskAsync(Request(), CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/v1/messages", fake.Last!.RequestUri!.ToString());
        Assert.Equal("key", fake.Last.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", fake.Last.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("server-side-fallback-2026-07-01", fake.Last.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("application/json", fake.Last.Content!.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(fake.Body!);
        var root = doc.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("web_search_20260209", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        var system = root.GetProperty("system").GetString()!;
        Assert.Contains("Отвечай только одним JSON-объектом по схеме", system);
        Assert.Contains("строго по-русски", system);
        Assert.Contains("\"additionalProperties\"", system);
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
    }

    [Fact]
    public async Task AskAsync_CustomModel_IsSentInBody()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        await new ClaudeClient(new HttpClient(fake), "key", null, new(" claude-sonnet-5-5 ")).AskAsync(Request(), CancellationToken.None);

        using var doc = JsonDocument.Parse(fake.Body!);
        Assert.Equal("claude-sonnet-5-5", doc.RootElement.GetProperty("model").GetString());
    }

    static (string? Model, string? Effort) ModelAndEffort(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("model").GetString(), root.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task AskAsync_TaskModelAndEffort_OverrideCommon()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        var choice = new ClaudeChoice("claude-sonnet-5-5", Ask: new() { Model = " claude-haiku-5-5 ", Effort = "high" });
        await new ClaudeClient(new HttpClient(fake), "key", null, choice).AskAsync(Request(), CancellationToken.None);

        Assert.Equal(("claude-haiku-5-5", "high"), ModelAndEffort(fake.Body!));
    }

    [Fact]
    public async Task LocateAsync_TaskWithoutModel_UsesCommonModelAndTaskEffort()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: LocationJson));
        var choice = new ClaudeChoice("claude-sonnet-5-5", Locate: new() { Effort = "low" }, Ask: new() { Model = "x", Effort = "max" });
        await new ClaudeClient(new HttpClient(fake), "key", null, choice).LocateAsync(LocateReq(), CancellationToken.None);

        Assert.Equal(("claude-sonnet-5-5", "low"), ModelAndEffort(fake.Body!));
    }

    [Fact]
    public async Task BlankTaskSettings_KeepBuiltInModelAndEfforts()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: LocationJson));
        var blank = new ClaudeTask { Model = " ", Effort = "" };
        var client = new ClaudeClient(new HttpClient(fake), "key", null, new ClaudeChoice(null, blank, blank, blank));

        await client.LocateAsync(LocateReq(), CancellationToken.None);
        Assert.Equal(("claude-opus-5-5", "medium"), ModelAndEffort(fake.Body!));

        var read = new Fake(HttpStatusCode.OK, Response(text: """{"settings":[]}"""));
        await new ClaudeClient(new HttpClient(read), "key", null, new ClaudeChoice(null, blank, blank, blank)).ReadAsync(ReadReq(), CancellationToken.None);
        Assert.Equal(("claude-opus-5-5", "low"), ModelAndEffort(read.Body!));
    }

    [Fact]
    public async Task AskAsync_CustomBaseUrl_PostsToProxy()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        await new ClaudeClient(new HttpClient(fake), "key", "https://claude.nl.danmet.ru:8443/").AskAsync(Request(), CancellationToken.None);

        Assert.Equal("https://claude.nl.danmet.ru:8443/v1/messages", fake.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task AskAsync_ValidResponse_ParsesResult()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        var result = await new ClaudeClient(new HttpClient(fake), "key").AskAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.Settings.Count);
        Assert.Equal("sg.ShadowQuality", result.Settings[0].Key);
        Assert.Null(result.Notes);
    }

    static string Blocks(params object[] content) => JsonSerializer.Serialize(new { stop_reason = "end_turn", content });

    [Fact]
    public void Parse_TextSplitAfterToolResult_ConcatenatesAndIgnoresLeadingText()
    {
        var cut = ResultJson.IndexOf("\"settings\"");
        var json = Blocks(
            new { type = "text", text = "Ищу материалы..." },
            new { type = "server_tool_use", name = "web_search" },
            new { type = "web_search_tool_result", content = "x" },
            new { type = "text", text = ResultJson[..cut] },
            new { type = "text", text = ResultJson[cut..] });

        var result = ClaudeClient.Parse<AdviceResult>(json);

        Assert.Equal(2, result.Settings.Count);
        Assert.Equal("s", result.Summary);
    }

    [Fact]
    public void Parse_NoToolBlocks_ConcatenatesAllText()
    {
        var cut = ResultJson.IndexOf("\"settings\"");
        var json = Blocks(new { type = "text", text = ResultJson[..cut] }, new { type = "text", text = ResultJson[cut..] });

        Assert.Equal(2, ClaudeClient.Parse<AdviceResult>(json).Settings.Count);
    }

    [Fact]
    public void Parse_FencedJson_Parses()
    {
        var json = Blocks(new { type = "text", text = "```json\n" + ResultJson + "\n```" });

        Assert.Equal(2, ClaudeClient.Parse<AdviceResult>(json).Settings.Count);
    }

    [Fact]
    public void Parse_PauseTurn_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse<AdviceResult>(Response("pause_turn")));
        Assert.Contains("Поиск не завершился", ex.Message);
    }

    [Fact]
    public void Parse_Refusal_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse<AdviceResult>(Response("refusal")));
        Assert.Contains("отказалась", ex.Message);
    }

    [Fact]
    public void Parse_MaxTokens_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse<AdviceResult>(Response("max_tokens")));
        Assert.Contains("оборвался", ex.Message);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        var bad = """{"stop_reason":"end_turn","content":[{"type":"text","text":"not json"}]}""";
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse<AdviceResult>(bad));
        Assert.Contains("неразборчивый", ex.Message);
    }

    static ReadRequest ReadReq() => new(new Game("G", @"C:\g", GameSource.Steam, "g.exe"), @"C:\g\a.ini", "ini", "k=1", false);

    [Fact]
    public void BuildReadBody_NoToolsLowEffortWithConfig()
    {
        var body = ClaudeClient.BuildReadBody(ReadReq(), "m");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(4000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("k=1", UserJson(body).GetProperty("config").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadAsync_ParsesSettings()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: """{"settings":[{"name":"Shadows","current":"High","section":"S","key":"k"}]}"""));
        var result = await new ClaudeClient(new HttpClient(fake), "key").ReadAsync(ReadReq(), CancellationToken.None);

        Assert.Equal(new CurrentSetting("Shadows", "High", "S", "k"), result.Settings.Single());
    }

    [Fact]
    public async Task ReadAsync_NullSettings_EmptyList()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: """{"settings":null}"""));
        var result = await new ClaudeClient(new HttpClient(fake), "key").ReadAsync(ReadReq(), CancellationToken.None);

        Assert.Empty(result.Settings);
    }

    [Fact]
    public void BuildAskBody_CurrentPassedOrNull()
    {
        var with = UserJson(ClaudeClient.BuildAskBody(Request() with { Current = [new("Shadows", "High", "S", "k")] }, "m"));
        Assert.Equal("Shadows", with.GetProperty("current")[0].GetProperty("name").GetString());
        var none = UserJson(ClaudeClient.BuildAskBody(Request(), "m"));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("current").ValueKind);
    }

    [Fact]
    public async Task AskAsync_429_ThrowsOverloaded()
    {
        var fake = new Fake((HttpStatusCode)429, "{}");
        var ex = await Assert.ThrowsAsync<AdviceException>(() =>
            new ClaudeClient(new HttpClient(fake), "key").AskAsync(Request(), CancellationToken.None));
        Assert.Contains("перегружен", ex.Message);
    }

    static AdjustRequest Adjust(IReadOnlyList<ChatTurn>? history = null, string text = "ещё выше", params ChatAttachment[] attachments) => new(
        Request(), new AdviceResult("sum", [new SettingAdvice("Shadows", "High", "Low", "S", "k", "0", "r")], null),
        history ?? [], text, attachments);

    static ChatTurn Turn(ChatRole role, string text, params string[] files) =>
        new(role, text, files.Length > 0 ? files.ToList() : null, DateTime.UtcNow);

    static List<JsonElement> Messages(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
    }

    static string Block(JsonElement msg, int i) => msg.GetProperty("content")[i].GetProperty("text").GetString()!;

    [Fact]
    public void BuildAdjustBody_RolesAlternate_ContextFirst()
    {
        var history = new[] { Turn(ChatRole.User, "тени выше"), Turn(ChatRole.Assistant, "поднял тени") };
        var msgs = Messages(ClaudeClient.BuildAdjustBody(Adjust(history), "m"));

        Assert.Equal(["user", "assistant", "user"], msgs.Select(m => m.GetProperty("role").GetString()));
        using var ctx = JsonDocument.Parse(Block(msgs[0], 0));
        Assert.Equal("sum", ctx.RootElement.GetProperty("advice").GetProperty("summary").GetString());
        Assert.Equal("balanced", ctx.RootElement.GetProperty("goal").GetProperty("mode").GetString());
        Assert.Equal(@"C:\g\a.ini", ctx.RootElement.GetProperty("config").GetProperty("path").GetString());
        Assert.Equal("тени выше", Block(msgs[0], 1));
        Assert.Equal("ещё выше", Block(msgs[2], 0));

        var first = Messages(ClaudeClient.BuildAdjustBody(Adjust(), "m")).Single();
        Assert.Equal(2, first.GetProperty("content").GetArrayLength());
        Assert.Equal("ещё выше", Block(first, 1));
        using var ctx2 = JsonDocument.Parse(Block(first, 0));
        Assert.Equal("sum", ctx2.RootElement.GetProperty("advice").GetProperty("summary").GetString());
    }

    [Fact]
    public void BuildAdjustBody_HistoryTrimmedTo20_StartsWithUser()
    {
        var history = Enumerable.Range(0, 24)
            .Select(i => Turn(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, "t" + i)).ToList();
        var msgs = Messages(ClaudeClient.BuildAdjustBody(Adjust(history), "m"));

        Assert.Equal(21, msgs.Count);
        Assert.Equal("user", msgs[0].GetProperty("role").GetString());
        Assert.Equal("t4", Block(msgs[0], 1));
        Assert.Contains("\"game\"", Block(msgs[0], 0));
    }

    [Fact]
    public void BuildAdjustBody_AttachmentBlocksInLastTurn()
    {
        var img = new ChatAttachment("a.png", new { type = "image", source = new { type = "base64", media_type = "image/png", data = "x" } });
        var txt = new ChatAttachment("a.log", new { type = "text", text = "Файл a.log:\nz" });
        var history = new[] { Turn(ChatRole.User, "см. меню", "menu.png"), Turn(ChatRole.Assistant, "ок") };
        var msgs = Messages(ClaudeClient.BuildAdjustBody(Adjust(history, "вот", img, txt), "m"));

        var last = msgs[^1].GetProperty("content");
        Assert.Equal(["text", "image", "text"], last.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("вот", last[0].GetProperty("text").GetString());
        Assert.Equal("см. меню\n[вложения: menu.png]", Block(msgs[0], 1));
    }

    [Fact]
    public void BuildAdjustBody_EmptyText_OnlyAttachmentBlocks()
    {
        var img = new ChatAttachment("a.png", new { type = "image", source = new { type = "base64", media_type = "image/png", data = "x" } });
        var history = new[] { Turn(ChatRole.User, "q"), Turn(ChatRole.Assistant, "a") };
        var last = Messages(ClaudeClient.BuildAdjustBody(Adjust(history, "", img), "m"))[^1].GetProperty("content");

        Assert.Equal(1, last.GetArrayLength());
        Assert.Equal("image", last[0].GetProperty("type").GetString());
    }

    const string AdjustJson = """{"reply":"Поднял тени","summary":"s","settings":[{"name":"Shadows","current":"High","recommended":"Low","section":"S","key":"k","value":"0","reason":"r"}],"notes":null}""";

    [Fact]
    public async Task BuildAdjustBody_ModelEffortSearch()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: AdjustJson));
        var choice = new ClaudeChoice(null, Ask: new() { Model = "claude-sonnet-5-5", Effort = "high" });
        await new ClaudeClient(new HttpClient(fake), "key", null, choice).AdjustAsync(Adjust(), CancellationToken.None);

        Assert.Equal(("claude-sonnet-5-5", "high"), ModelAndEffort(fake.Body!));
        using var doc = JsonDocument.Parse(fake.Body!);
        Assert.Equal(2, doc.RootElement.GetProperty("tools")[0].GetProperty("max_uses").GetInt32());
        Assert.Equal(8000, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Contains("reply", doc.RootElement.GetProperty("system").GetString());

        var plain = new Fake(HttpStatusCode.OK, Response(text: AdjustJson));
        await new ClaudeClient(new HttpClient(plain), "key").AdjustAsync(Adjust(), CancellationToken.None);
        Assert.Equal(("claude-opus-5-5", "medium"), ModelAndEffort(plain.Body!));
    }

    [Fact]
    public async Task AdjustAsync_ParsesReplyAndSettings()
    {
        var fake = new Fake(HttpStatusCode.OK, Response(text: AdjustJson));
        var result = await new ClaudeClient(new HttpClient(fake), "key").AdjustAsync(Adjust(), CancellationToken.None);

        Assert.Equal("Поднял тени", result.Reply);
        Assert.Equal("Shadows", result.Settings.Single().Name);
    }
}
