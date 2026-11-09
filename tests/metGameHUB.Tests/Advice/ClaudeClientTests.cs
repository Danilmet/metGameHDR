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
        await new ClaudeClient(new HttpClient(fake), "key", null, " claude-sonnet-5-5 ").AskAsync(Request(), CancellationToken.None);

        using var doc = JsonDocument.Parse(fake.Body!);
        Assert.Equal("claude-sonnet-5-5", doc.RootElement.GetProperty("model").GetString());
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

    [Fact]
    public async Task AskAsync_429_ThrowsOverloaded()
    {
        var fake = new Fake((HttpStatusCode)429, "{}");
        var ex = await Assert.ThrowsAsync<AdviceException>(() =>
            new ClaudeClient(new HttpClient(fake), "key").AskAsync(Request(), CancellationToken.None));
        Assert.Contains("перегружен", ex.Message);
    }
}
