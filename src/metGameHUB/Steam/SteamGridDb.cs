using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace MetGameHUB.Steam;

public sealed class SteamGridDb(HttpClient http, string apiKey)
{
    const string BaseUrl = "https://www.steamgriddb.com/api/v2/";

    public async Task<int?> SearchAsync(string name)
    {
        using var doc = await GetAsync("search/autocomplete/" + Uri.EscapeDataString(name));
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() == 0 ? null : data[0].GetProperty("id").GetInt32();
    }

    public async Task<string?> BestImageUrlAsync(string kind, int gameId, string? dimensions)
    {
        var path = $"{kind}/game/{gameId}" + (dimensions is null ? "" : "?dimensions=" + dimensions);
        using var doc = await GetAsync(path);
        string? best = null;
        var bestScore = double.MinValue;
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var score = item.GetProperty("score").GetDouble();
            if (score > bestScore)
            {
                bestScore = score;
                best = item.GetProperty("url").GetString();
            }
        }
        return best;
    }

    async Task<JsonDocument> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    }
}
