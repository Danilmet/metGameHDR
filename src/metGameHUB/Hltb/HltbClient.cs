using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetGameHUB.Scan;

namespace MetGameHUB.Hltb;

public sealed record HltbHit(int Id, string Name, string Type, int Main, int Plus, int Full);

public sealed class HltbClient(HttpClient http)
{
    const string Site = "https://howlongtobeat.com/";
    // The token is bound to IP and User-Agent, so the UA must stay constant for the process lifetime.
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    static readonly CultureInfo Ru = new("ru-RU");

    string? token;

    public async Task<HltbInfo> FetchAsync(string name, DateTime now, CancellationToken ct = default)
    {
        string body;
        try { body = await SearchAsync(name, ct); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden)
        {
            token = null;
            body = await SearchAsync(name, ct);
        }
        var hit = Pick(Parse(body), name);
        return hit is null
            ? new HltbInfo(null, null, 0, 0, 0, name, now)
            : new HltbInfo(hit.Id, hit.Name, hit.Main, hit.Plus, hit.Full, name, now);
    }

    async Task<string> SearchAsync(string name, CancellationToken ct)
    {
        token ??= await InitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Site + "api/search/site");
        Headers(req);
        req.Headers.Add("x-auth-token", token);
        req.Content = new StringContent(SearchBody(name), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    async Task<string> InitAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Site + "api/search/site/init?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Headers(req);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var t = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["token"]?.GetValue<string>();
        return string.IsNullOrEmpty(t) ? throw new InvalidDataException("HLTB init: no token") : t;
    }

    static void Headers(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", Site);
        req.Headers.TryAddWithoutValidation("Origin", "https://howlongtobeat.com");
    }

    static string SearchBody(string name) => new JsonObject
    {
        ["searchType"] = "games",
        ["searchTerms"] = new JsonArray(SearchTerms(name).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
        ["searchPage"] = 1,
        ["size"] = 20,
        ["searchOptions"] = new JsonObject
        {
            ["games"] = new JsonObject
            {
                ["userId"] = 0,
                ["platform"] = "",
                ["sortCategory"] = "popular",
                ["rangeCategory"] = "main",
                ["rangeTime"] = new JsonObject { ["min"] = 0, ["max"] = 0 },
                ["gameplay"] = new JsonObject { ["perspective"] = "", ["flow"] = "", ["genre"] = "", ["difficulty"] = "" },
                ["rangeYear"] = new JsonObject { ["max"] = "", ["min"] = "" },
                ["modifier"] = "",
            },
            ["users"] = new JsonObject { ["sortCategory"] = "postcount" },
            ["lists"] = new JsonObject { ["sortCategory"] = "follows" },
            ["filter"] = "",
            ["sort"] = 0,
            ["randomizer"] = 0,
        },
        ["useCache"] = true,
    }.ToJsonString();

    static string StripMarks(string s) => s.Replace("™", "").Replace("®", "").Replace("©", "");

    public static string[] SearchTerms(string name) => StripMarks(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static List<HltbHit> Parse(string json)
    {
        if (JsonNode.Parse(json)?["data"] is not JsonArray data) throw new InvalidDataException("HLTB: no data array");
        return data.Select(n => new HltbHit(
            n!["game_id"]!.GetValue<int>(),
            n["game_name"]?.GetValue<string>() ?? "",
            n["game_type"]?.GetValue<string>() ?? "",
            Secs(n["comp_main"]), Secs(n["comp_plus"]), Secs(n["comp_100"]))).ToList();
    }

    static int Secs(JsonNode? n) => n is null ? 0 : n.GetValue<int>();

    public static HltbHit? Pick(IReadOnlyList<HltbHit> hits, string name)
    {
        var games = hits.Where(h => h.Type == "game").ToList();
        var key = Normalize(name);
        return games.FirstOrDefault(h => Normalize(h.Name) == key) ?? games.FirstOrDefault();
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in StripMarks(s.ToLowerInvariant())) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool IsFresh(HltbInfo? info, string query, DateTime now) =>
        info is not null && info.Query == query && now - info.Fetched <= TimeSpan.FromDays(info.Id is null ? 7 : 30);

    public static string Format(HltbInfo info)
    {
        if (info.Id is null) return "HLTB: не найдено";
        var parts = new List<string>();
        if (info.Main > 0) parts.Add("сюжет " + Hours(info.Main));
        if (info.Plus > 0) parts.Add("+ доп. " + Hours(info.Plus));
        if (info.Full > 0) parts.Add("100% " + Hours(info.Full));
        return parts.Count == 0 ? "HLTB: нет данных" : "HLTB: " + string.Join(" · ", parts);
    }

    static string Hours(int seconds)
    {
        var min = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        if (min < 60) return min + " мин";
        var h = seconds / 3600.0;
        return h < 10
            ? (Math.Round(h * 2, MidpointRounding.AwayFromZero) / 2).ToString("0.#", Ru) + " ч"
            : Math.Round(h, MidpointRounding.AwayFromZero).ToString("0", Ru) + " ч";
    }

    public static string GameUrl(int id) => $"{Site}game/{id}";
}
