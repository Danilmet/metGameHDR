using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetGameHDR.Advice;
using MetGameHDR.Scan;
using Xunit;

namespace MetGameHDR.Tests;

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
        {"summary":"s","settings":[{"name":"Shadows","value":"Low","reason":"r"},{"name":"DLSS","value":"Quality","reason":"r"}],
         "config":{"path":"C:\\g\\a.ini","edits":[{"section":null,"key":"k","value":"v"}]},"notes":null}
        """;

    static string Response(string stopReason = "end_turn") => JsonSerializer.Serialize(new
    {
        stop_reason = stopReason,
        content = new object[]
        {
            new { type = "server_tool_use", name = "web_search" },
            new { type = "web_search_tool_result", content = "x" },
            new { type = "text", text = ResultJson },
        },
    });

    static AdviceRequest Request() => new(
        new Game("G", @"C:\g", GameSource.Steam, "g.exe"),
        new HardwareInfo("RTX", 8, "CPU", 16, 2560, 1440, 144, "Win11"),
        new GraphicsGoal(60, Priority.Balanced),
        [new ConfigCandidate(@"C:\g\a.ini", "k=1", false)]);

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
        Assert.False(root.TryGetProperty("output_config", out _));
        var system = root.GetProperty("system").GetString()!;
        Assert.Contains("Отвечай только одним JSON-объектом по этой схеме", system);
        Assert.Contains("\"additionalProperties\"", system);
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
    }

    [Fact]
    public async Task AskAsync_ValidResponse_ParsesResult()
    {
        var fake = new Fake(HttpStatusCode.OK, Response());
        var result = await new ClaudeClient(new HttpClient(fake), "key").AskAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.Settings.Count);
        Assert.Equal(@"C:\g\a.ini", result.Config!.Path);
        Assert.Null(result.Config.Edits[0].Section);
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

        var result = ClaudeClient.Parse(json);

        Assert.Equal(2, result.Settings.Count);
        Assert.Equal("s", result.Summary);
    }

    [Fact]
    public void Parse_NoToolBlocks_ConcatenatesAllText()
    {
        var cut = ResultJson.IndexOf("\"settings\"");
        var json = Blocks(new { type = "text", text = ResultJson[..cut] }, new { type = "text", text = ResultJson[cut..] });

        Assert.Equal(2, ClaudeClient.Parse(json).Settings.Count);
    }

    [Fact]
    public void Parse_FencedJson_Parses()
    {
        var json = Blocks(new { type = "text", text = "```json\n" + ResultJson + "\n```" });

        Assert.Equal(2, ClaudeClient.Parse(json).Settings.Count);
    }

    [Fact]
    public void Parse_PauseTurn_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse(Response("pause_turn")));
        Assert.Contains("Поиск не завершился", ex.Message);
    }

    [Fact]
    public void Parse_Refusal_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse(Response("refusal")));
        Assert.Contains("отказалась", ex.Message);
    }

    [Fact]
    public void Parse_MaxTokens_Throws()
    {
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse(Response("max_tokens")));
        Assert.Contains("оборвался", ex.Message);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        var bad = """{"stop_reason":"end_turn","content":[{"type":"text","text":"not json"}]}""";
        var ex = Assert.Throws<AdviceException>(() => ClaudeClient.Parse(bad));
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
