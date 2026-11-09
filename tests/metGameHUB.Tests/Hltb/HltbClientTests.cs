using System.Net;
using System.Net.Http;
using MetGameHUB.Hltb;
using MetGameHUB.Scan;
using Xunit;

namespace MetGameHUB.Tests;

public class HltbClientTests
{
    const string CyberpunkJson = """{"data":[{"game_id":2127,"game_name":"Cyberpunk 2077","game_type":"game","comp_main":94172,"comp_plus":227747,"comp_100":null}]}""";
    static readonly DateTime Now = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> search) : HttpMessageHandler
    {
        public int Inits;
        public HttpRequestMessage? InitReq, SearchReq;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/init"))
            {
                Inits++;
                InitReq = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t" + Inits + "\"}") });
            }
            SearchReq = request;
            return Task.FromResult(search(request));
        }
    }

    static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    static HltbHit H(string name, string type = "game") => new(1, name, type, 0, 0, 0);

    static HltbInfo Info(int? id, int main = 0, int plus = 0, int full = 0, string q = "q") =>
        new(id, id is null ? null : "X", main, plus, full, q, Now);

    [Fact] public void Parse_ReadsSecondsAndMissingAsZero()
    {
        var hit = Assert.Single(HltbClient.Parse(CyberpunkJson));
        Assert.Equal(new HltbHit(2127, "Cyberpunk 2077", "game", 94172, 227747, 0), hit);
        var noPlus = HltbClient.Parse("""{"data":[{"game_id":1,"game_name":"A","game_type":"game","comp_main":5}]}""");
        Assert.Equal(0, noPlus[0].Plus);
    }

    [Fact] public void Parse_ErrorBodyThrows() =>
        Assert.ThrowsAny<Exception>(() => HltbClient.Parse("""{"error":"Session expired or invalid fingerprint"}"""));

    [Fact] public void SearchTerms_StripsMarksAndEmpty() =>
        Assert.Equal(["Tom", "Clancy's", "Rainbow", "Six", "Siege"], HltbClient.SearchTerms("Tom Clancy's  Rainbow Six® Siege"));

    [Fact] public void Pick_PrefersExactNormalizedName() =>
        Assert.Equal("Cyberpunk 2077", HltbClient.Pick([H("Cyberpunk 2077: Phantom Liberty"), H("Cyberpunk 2077")], "Cyberpunk 2077™")!.Name);

    [Fact] public void Pick_FallsBackToFirstGame() =>
        Assert.Equal("B", HltbClient.Pick([H("A", "dlc"), H("B"), H("C")], "Zzz")!.Name);

    [Fact] public void Pick_SkipsDlc() =>
        Assert.Equal("Cyberpunk 2077 Ultimate", HltbClient.Pick([H("Cyberpunk 2077", "dlc"), H("Cyberpunk 2077 Ultimate")], "Cyberpunk 2077")!.Name);

    [Fact] public void Pick_EmptyReturnsNull()
    {
        Assert.Null(HltbClient.Pick([], "A"));
        Assert.Null(HltbClient.Pick([H("A", "dlc")], "A"));
    }

    [Fact] public void IsFresh_QueryChangedIsStale()
    {
        Assert.False(HltbClient.IsFresh(Info(1, q: "A"), "B", Now));
        Assert.False(HltbClient.IsFresh(null, "A", Now));
    }

    [Fact] public void IsFresh_FoundExpiresAfter30Days()
    {
        Assert.True(HltbClient.IsFresh(Info(1), "q", Now.AddDays(29)));
        Assert.False(HltbClient.IsFresh(Info(1), "q", Now.AddDays(31)));
    }

    [Fact] public void IsFresh_NotFoundExpiresAfter7Days()
    {
        Assert.True(HltbClient.IsFresh(Info(null), "q", Now.AddDays(6)));
        Assert.False(HltbClient.IsFresh(Info(null), "q", Now.AddDays(8)));
    }

    [Fact] public void Format_HoursRounding()
    {
        Assert.Equal("HLTB: сюжет 45 мин · + доп. 2,5 ч · 100% 26 ч", HltbClient.Format(Info(1, 2700, 9000, 94172)));
        Assert.Equal("HLTB: сюжет 3 ч", HltbClient.Format(Info(1, 10800)));
        Assert.Equal("HLTB: нет данных", HltbClient.Format(Info(1)));
        Assert.Equal("HLTB: не найдено", HltbClient.Format(Info(null)));
    }

    [Fact] public async Task FetchAsync_SendsTokenAndPicks()
    {
        var fake = new Fake(_ => Ok(CyberpunkJson));
        var info = await new HltbClient(new HttpClient(fake)).FetchAsync("Cyberpunk 2077", Now);
        Assert.Equal(2127, info.Id);
        Assert.Equal("Cyberpunk 2077", info.Query);
        var req = fake.SearchReq!;
        Assert.Equal("t1", req.Headers.GetValues("x-auth-token").Single());
        Assert.Equal("https://howlongtobeat.com/", req.Headers.GetValues("Referer").Single());
        Assert.Equal("https://howlongtobeat.com", req.Headers.GetValues("Origin").Single());
        Assert.Equal(fake.InitReq!.Headers.UserAgent.ToString(), req.Headers.UserAgent.ToString());
        Assert.NotEmpty(req.Headers.UserAgent);
    }

    [Fact] public async Task FetchAsync_RetriesOnceOn403()
    {
        var calls = 0;
        var fake = new Fake(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Ok(CyberpunkJson));
        var info = await new HltbClient(new HttpClient(fake)).FetchAsync("Cyberpunk 2077", Now);
        Assert.Equal(2, fake.Inits);
        Assert.Equal(2127, info.Id);

        var always = new Fake(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Assert.ThrowsAnyAsync<Exception>(() => new HltbClient(new HttpClient(always)).FetchAsync("A", Now));
    }

    [Fact] public async Task FetchAsync_NoMatchReturnsNullId()
    {
        var fake = new Fake(_ => Ok("""{"data":[]}"""));
        var info = await new HltbClient(new HttpClient(fake)).FetchAsync("Nothing", Now);
        Assert.Null(info.Id);
        Assert.Equal(Now, info.Fetched);
    }
}
