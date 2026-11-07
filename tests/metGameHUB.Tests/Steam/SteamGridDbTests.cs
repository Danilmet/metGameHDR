using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MetGameHUB.Steam;
using Xunit;

namespace MetGameHUB.Tests.Steam;

public class SteamGridDbTests
{
    sealed class Fake((string urlSuffix, string json)[] routes) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            var url = request.RequestUri!.OriginalString;
            foreach (var (suffix, json) in routes)
                if (url.EndsWith(suffix))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static SteamGridDb Db(params (string urlSuffix, string json)[] routes) => Db(out _, routes);

    static SteamGridDb Db(out Fake fake, params (string urlSuffix, string json)[] routes)
    {
        fake = new Fake(routes);
        return new SteamGridDb(new HttpClient(fake), "k");
    }

    [Fact]
    public async Task SearchAsync_ReturnsFirstId()
    {
        var db = Db(("search/autocomplete/Half%20Life", """{"success":true,"data":[{"id":42,"name":"Half-Life"},{"id":7}]}"""));
        Assert.Equal(42, await db.SearchAsync("Half Life"));
    }

    [Fact]
    public async Task BestImageUrlAsync_PicksMaxScoreWithDimensions()
    {
        var db = Db(("grids/game/42?dimensions=600x900", """{"success":true,"data":[{"score":1,"url":"http://x/a.png"},{"score":5,"url":"http://x/b.jpg"}]}"""));
        Assert.Equal("http://x/b.jpg", await db.BestImageUrlAsync("grids", 42, "600x900"));
    }

    [Fact]
    public async Task BestImageUrlAsync_EmptyReturnsNull()
    {
        var db = Db(("heroes/game/42", """{"success":true,"data":[]}"""));
        Assert.Null(await db.BestImageUrlAsync("heroes", 42, null));
    }

    [Fact]
    public async Task Search_SendsBearerHeader()
    {
        var db = Db(out var fake, ("search/autocomplete/x", """{"success":true,"data":[{"id":1}]}"""));
        await db.SearchAsync("x");
        Assert.Equal("Bearer k", fake.Last!.Headers.Authorization!.ToString());
    }
}
