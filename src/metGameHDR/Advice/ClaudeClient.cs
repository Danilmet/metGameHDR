using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetGameHDR.Scan;

namespace MetGameHDR.Advice;

public sealed record AdviceRequest(Game Game, HardwareInfo Hardware, GraphicsGoal Goal, IReadOnlyList<ConfigCandidate> Candidates);

public sealed class AdviceException(string message) : Exception(message);

public sealed class ClaudeClient(HttpClient http, string apiKey, string? baseUrl = null)
{
    public const string Model = "claude-opus-5-5";
    const string DefaultBaseUrl = "https://api.anthropic.com";

    const string SystemPrompt = """
        Ты эксперт по оптимизации графических настроек ПК-игр. Тебе дают игру, железо пользователя, цель (кадры в секунду и приоритет: картинка, баланс или отзывчивость) и содержимое найденных файлов настроек игры.
        Правила:
        1. Найди свежие материалы по оптимизации именно этой игры (статьи, тесты, форумы) и опирайся на них и на известное поведение движка.
        2. В settings перечисли настройки так, как они называются в меню игры, со значением и причиной в одну фразу. Разрешение и апскейлер указывай под монитор пользователя. Цель по кадрам — для типичных сцен, с запасом ~10%.
        3. config заполняй только если один из присланных файлов действительно хранит графические настройки: path — точно как прислан; в edits используй только ключи, которые видишь в файле или которые документированы для этого движка. Ключи не выдумывай. Если подходящего файла нет — config: null.
        4. summary, reason и notes — строго по-русски, даже если источники на английском; name и value — так, как они написаны в меню игры (на языке, на котором у пользователя игра; если неизвестно — по-английски). summary — 2–3 предложения о том, что упирается в железо и чем пожертвовали.
        """;

    const string JsonOnly = "Отвечай только одним JSON-объектом по этой схеме, без пояснений до или после и без markdown-ограждений:";

    const string Schema = """
        {
          "type": "object", "additionalProperties": false,
          "required": ["summary", "settings", "config", "notes"],
          "properties": {
            "summary": { "type": "string" },
            "settings": { "type": "array", "items": { "type": "object", "additionalProperties": false,
              "required": ["name", "value", "reason"],
              "properties": { "name": {"type":"string"}, "value": {"type":"string"}, "reason": {"type":"string"} } } },
            "config": { "anyOf": [ { "type": "null" }, { "type": "object", "additionalProperties": false,
              "required": ["path", "edits"],
              "properties": { "path": {"type":"string"}, "edits": { "type": "array", "items": { "type": "object", "additionalProperties": false,
                "required": ["section", "key", "value"],
                "properties": { "section": { "type": ["string","null"] }, "key": {"type":"string"}, "value": {"type":"string"} } } } } } ] },
            "notes": { "type": ["string", "null"] }
          }
        }
        """;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/')) + "/v1/messages")
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2026-10-21");
        req.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            throw new AdviceException(code is 429 or 529
                ? "Сервис перегружен, повторите позже"
                : $"Ошибка API {code}: {(text.Length > 200 ? text[..200] : text)}");
        }
        return Parse(text);
    }

    internal static string BuildBody(AdviceRequest request)
    {
        var user = new
        {
            game = new { name = request.Game.Name, source = request.Game.Source, installDir = request.Game.InstallDir, exe = request.Game.MainExe },
            hardware = request.Hardware,
            goal = request.Goal,
            candidates = request.Candidates,
        };
        using var schema = JsonDocument.Parse(Schema);
        return JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = 8000,
            system = $"{SystemPrompt}\n{JsonOnly}\n{JsonSerializer.Serialize(schema.RootElement)}",
            tools = new[] { new { type = "web_search_20260209", name = "web_search", max_uses = 5 } },
            fallbacks = "default",
            messages = new[] { new { role = "user", content = JsonSerializer.Serialize(user, Json) } },
        }, Json);
    }

    internal static AdviceResult Parse(string responseJson)
    {
        const string garbled = "Модель вернула неразборчивый ответ";
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var stop = root.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
            if (stop == "refusal") throw new AdviceException("Модель отказалась отвечать");
            if (stop == "max_tokens") throw new AdviceException("Ответ оборвался, повторите");
            if (stop == "pause_turn") throw new AdviceException("Поиск не завершился, повторите запрос");

            var blocks = root.GetProperty("content").EnumerateArray().ToList();
            var afterTools = blocks.FindLastIndex(b =>
            {
                var t = b.GetProperty("type").GetString()!;
                return t == "server_tool_use" || t.EndsWith("_tool_result");
            }) + 1;
            var answer = string.Concat(blocks.Skip(afterTools)
                .Where(b => b.GetProperty("type").GetString() == "text")
                .Select(b => b.GetProperty("text").GetString())).Trim();
            if (answer.StartsWith("```"))
            {
                var nl = answer.IndexOf('\n');
                answer = (nl < 0 ? "" : answer[(nl + 1)..]).TrimEnd();
                if (answer.EndsWith("```")) answer = answer[..^3];
            }
            return JsonSerializer.Deserialize<AdviceResult>(answer, Json) ?? throw new AdviceException(garbled);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AdviceException(garbled);
        }
    }
}
