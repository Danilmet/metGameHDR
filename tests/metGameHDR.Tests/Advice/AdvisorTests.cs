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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    sealed class Env : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Install, Ini, LibPath;
        public Game Game;
        public Library Lib = new();
        public bool Running;

        public Env()
        {
            Install = Path.Combine(Dir, "game");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Path.Combine(Dir, "empty"));
            Ini = Path.Combine(Install, "a.ini");
            File.WriteAllText(Ini, "[S]\r\nA=1\r\n");
            LibPath = Path.Combine(Dir, "lib.json");
            Game = new Game("Game", Install, GameSource.Folder, "g.exe");
        }

        public FinderRoots Roots => new(Path.Combine(Dir, "empty"), Path.Combine(Dir, "empty"), Path.Combine(Dir, "empty"));

        public Advisor Advisor(string json = "{}") => new(() => "key", () => null, () => null, Lib, LibPath,
            () => new HardwareInfo("GPU", 8, "CPU", 16, 1920, 1080, 60, "Win"),
            Roots, _ => Running, new HttpClient(new Fake(json)));

        public Advice.Advice AdviceFor(string path, string value = "2") =>
            new(new AdviceResult("s", [], new ConfigAdvice(path, [new ConfigEdit("S", "A", value)]), null), DateTime.UtcNow);

        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task AskAsync_StoresAdviceOnOverrideAndSaves()
    {
        using var e = new Env();
        var result = JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text = """{"summary":"sum","settings":[],"config":null,"notes":null}""" } },
        });
        var goal = new GraphicsGoal(60, Priority.Quality);

        var advice = await e.Advisor(result).AskAsync(e.Game, goal, CancellationToken.None);

        Assert.Equal("sum", advice.Result.Summary);
        Assert.Same(advice, e.Lib.For(e.Game).Advice);
        Assert.Equal(goal, e.Lib.For(e.Game).Goal);
        Assert.Equal("sum", Library.Load(e.LibPath).For(e.Game).Advice!.Result.Summary);
    }

    [Fact]
    public void Apply_GameRunning_ReturnsGameRunning_AndFileUntouched()
    {
        using var e = new Env { Running = true };

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(e.Ini));

        Assert.Equal(ApplyOutcome.GameRunning, outcome);
        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini));
        Assert.False(File.Exists(e.Ini + ".orig"));
    }

    [Fact]
    public void Apply_PathWithDifferentCaseAndSlashes_StillApplies()
    {
        using var e = new Env();
        var path = e.Ini.ToUpperInvariant().Replace('\\', '/');

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(path));

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Contains("A=2", File.ReadAllText(e.Ini));
    }

    [Fact]
    public void Apply_PathOutsideCandidates_ReturnsPathNotCandidate()
    {
        using var e = new Env();
        var other = Path.Combine(e.Dir, "other.ini");
        File.WriteAllText(other, "[S]\r\nA=1\r\n");

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(other));

        Assert.Equal(ApplyOutcome.PathNotCandidate, outcome);
        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(other));
    }

    [Fact]
    public void Apply_Twice_KeepsFirstOrigAndRewritesBak()
    {
        using var e = new Env();
        var advisor = e.Advisor();
        advisor.Apply(e.Game, e.AdviceFor(e.Ini));

        advisor.Apply(e.Game, e.AdviceFor(e.Ini, "3"));

        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini + ".orig"));
        Assert.Contains("A=2", File.ReadAllText(e.Ini + ".bak"));
        Assert.Contains("A=3", File.ReadAllText(e.Ini));
    }

    [Fact]
    public void Apply_JsonFile_MissingKey_ReturnsNotIni()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "  {\"a\":1}");

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(e.Ini));

        Assert.Equal(ApplyOutcome.NotIni, outcome);
        Assert.Equal("  {\"a\":1}", File.ReadAllText(e.Ini));
        Assert.False(File.Exists(e.Ini + ".orig"));
    }

    [Fact]
    public void Apply_LineJsonWithAllKeys_Applies()
    {
        using var e = new Env();
        File.WriteAllText(e.Ini, "{\n\t\"A\": 1,\n\t\"B\": 2\n}\n");

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(e.Ini));

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
        var advice = new Advice.Advice(new AdviceResult("s", [], new ConfigAdvice(e.Ini, [new ConfigEdit("S", key, value)]), null), DateTime.UtcNow);

        var outcome = e.Advisor().Apply(e.Game, advice);

        Assert.Equal(ApplyOutcome.BadEdit, outcome);
        Assert.Equal("[S]\r\nA=1\r\n", File.ReadAllText(e.Ini));
    }

    [Fact]
    public void Apply_EditWithNewlineInValue_ReturnsBadEdit()
    {
        using var e = new Env();

        var outcome = e.Advisor().Apply(e.Game, e.AdviceFor(e.Ini, "2\nEvil=1"));

        Assert.Equal(ApplyOutcome.BadEdit, outcome);
    }

    [Fact]
    public async Task AskAsync_UsesOverrideNameAndExeInRequest()
    {
        using var e = new Env();
        var o = e.Lib.For(e.Game);
        o.SearchName = "Real Name";
        o.MainExe = "real.exe";
        var handler = new CapturingHandler();

        var advisor = new Advisor(() => "key", () => null, () => null, e.Lib, e.LibPath, () => new HardwareInfo("GPU", 8, "CPU", 16, 1920, 1080, 60, "Win"),
            e.Roots, _ => false, new HttpClient(handler));
        await Assert.ThrowsAnyAsync<Exception>(() => advisor.AskAsync(e.Game, new GraphicsGoal(60, Priority.Balanced), CancellationToken.None));

        Assert.Contains("Real Name", handler.Body);
        Assert.Contains("real.exe", handler.Body);
        Assert.DoesNotContain("g.exe", handler.Body);
    }

    sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public void Preview_ReturnsCurrentValues()
    {
        using var e = new Env();

        var preview = Advisor.Preview(e.AdviceFor(e.Ini));

        var p = Assert.Single(preview);
        Assert.Equal("1", p.Current);
        Assert.Equal("2", p.Edit.Value);
    }
}
