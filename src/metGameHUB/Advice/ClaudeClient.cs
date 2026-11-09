using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetGameHUB.Scan;

namespace MetGameHUB.Advice;

public sealed record LocateRequest(Game Game, IReadOnlyList<string> InstallRootEntries, IReadOnlyList<string> CandidatePaths, string? UnrealProject);

public sealed record AdviceRequest(Game Game, HardwareInfo Hardware, GoalStep Goal, string? ConfigPath, string? ConfigFormat, string? ConfigText, bool Truncated, List<CurrentSetting>? Current = null);

public sealed record AdjustRequest(AdviceRequest Context, AdviceResult Advice, IReadOnlyList<ChatTurn> History, string Text, IReadOnlyList<ChatAttachment> Attachments);

public sealed record AdjustResult(string Reply, string Summary, List<SettingAdvice> Settings, string? Notes);

public sealed record ReadRequest(Game Game, string ConfigPath, string ConfigFormat, string ConfigText, bool Truncated);

public sealed record ReadResult(List<CurrentSetting> Settings);

public sealed class AdviceException(string message) : Exception(message);

public sealed class ClaudeTask
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
}

/// <summary>Общая модель и переопределения по задачам; пустое значение — уровнем выше (задача → общая → встроенная).</summary>
public sealed record ClaudeChoice(string? Model, ClaudeTask? Locate = null, ClaudeTask? Read = null, ClaudeTask? Ask = null);

public sealed class ClaudeClient(HttpClient http, string apiKey, string? baseUrl = null, ClaudeChoice? choice = null)
{
    public const string Model = "claude-opus-5-5";
    public const int MaxHistoryTurns = 20;
    const string LocateEffort = "medium", AskEffort = "medium", ReadEffort = "low";
    const string DefaultBaseUrl = "https://api.anthropic.com";

    const string LocatePrompt = """
        Ты знаешь, где ПК-игры хранят файлы графических настроек. Тебе дают игру (имя, папку установки, exe), список файлов и папок первого уровня в папке установки, найденные приложением файлы-кандидаты и, если это Unreal Engine, имя проекта.
        Правила:
        1. Назови ОДИН файл, в котором игра хранит графические настройки меню (разрешение, качество, апскейлер). Путь — абсолютный для Windows, допускаются переменные %LocalAppData%, %AppData%, %UserProfile%, %ProgramData% и плейсхолдер <InstallDir>. Если нужный файл есть среди кандидатов — верни его путь как есть.
        2. format: "ini" для INI/CFG с парами ключ=значение или ключ значение; "json" для JSON; "none", если игра хранит настройки в реестре, в бинарном файле или в облаке — тогда path: null.
        3. Если файл обычно появляется только после первого запуска игры, всё равно назови путь.
        4. note — одна фраза по-русски, что это за файл или почему его нет.
        Отвечай только одним JSON-объектом по схеме, без пояснений и без markdown-ограждений:
        {"type":"object","additionalProperties":false,"required":["path","format","note"],"properties":{"path":{"type":["string","null"]},"format":{"type":"string","enum":["ini","json","none"]},"note":{"type":["string","null"]}}}
        """;

    const string AskPrompt = """
        Ты эксперт по оптимизации графических настроек ПК-игр. Тебе дают игру, железо пользователя, цель (режим и кадры в секунду) и, если найден, файл настроек игры с его содержимым и, если есть, уже разобранные текущие значения (current).
        Правила:
        1. Найди свежие материалы по оптимизации именно этой игры (статьи, тесты, форумы) и опирайся на них и на известное поведение движка.
        2. goal.mode задаёт, чем жертвовать: max_quality — максимум картинки, кадры как получатся, но не ниже goal.fps; quality — стабильно держать goal.fps, весь остальной запас отдать картинке; balanced — держать goal.fps в типичных сценах, жертвовать самым дорогим при малой разнице в картинке; smooth — держать goal.fps с запасом и по минимальным кадрам (1% low), картинка во вторую очередь; max_fps — как можно больше кадров и минимальная задержка, картинка вторична (goal.fps = null). Для quality, balanced и smooth цель по кадрам — для типичных сцен, с запасом ~10%.
        3. settings — один список строк по пунктам графического меню игры. name и value-поля пиши так, как они видны в меню игры (на языке игры, если неизвестен — по-английски). Разрешение и апскейлер указывай под монитор пользователя. Если прислан current — это уже разобранные пункты файла: бери из него name и current для этих пунктов.
        4. Для каждого пункта: current — текущее значение, разобранное из присланного файла и переведённое в подпись меню (например ключ 3 → "High"); если файла нет или пункт в нём не хранится — null. recommended — рекомендуемая подпись меню.
        5. Если пункт хранится в присланном файле, заполни section (секция INI или null), key (точное имя ключа из файла; для INI допустим документированный для движка ключ, которого в файле нет) и value — сырое значение для записи в файл в том виде, в каком оно там пишется (число, true/false, строка без лишних кавычек). Иначе section/key/value — null. Ключи не выдумывай.
        6. summary, reason и notes — строго по-русски. summary — 2–3 предложения о том, что упирается в железо и чем пожертвовали. notes — предупреждения (например, что часть настроек надо выставить руками), иначе null.
        Отвечай только одним JSON-объектом по схеме, без пояснений и без markdown-ограждений:
        {"type":"object","additionalProperties":false,"required":["summary","settings","notes"],"properties":{"summary":{"type":"string"},"settings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","current","recommended","section","key","value","reason"],"properties":{"name":{"type":"string"},"current":{"type":["string","null"]},"recommended":{"type":"string"},"section":{"type":["string","null"]},"key":{"type":["string","null"]},"value":{"type":["string","null"]},"reason":{"type":"string"}}}},"notes":{"type":["string","null"]}}}
        """;

    const string AdjustPrompt = """
        Ты эксперт по оптимизации графических настроек ПК-игр. Тебе дают игру, железо, цель, файл настроек (если найден), текущие значения (current), прошлый совет (advice) и переписку с пользователем.
        Правила:
        1. Найди свежие материалы по оптимизации именно этой игры (статьи, тесты, форумы) и опирайся на них и на известное поведение движка.
        2. goal.mode задаёт, чем жертвовать: max_quality — максимум картинки, кадры как получатся, но не ниже goal.fps; quality — стабильно держать goal.fps, весь остальной запас отдать картинке; balanced — держать goal.fps в типичных сценах, жертвовать самым дорогим при малой разнице в картинке; smooth — держать goal.fps с запасом и по минимальным кадрам (1% low), картинка во вторую очередь; max_fps — как можно больше кадров и минимальная задержка, картинка вторична (goal.fps = null). Для quality, balanced и smooth цель по кадрам — для типичных сцен, с запасом ~10%.
        3. settings — один список строк по пунктам графического меню игры. name и value-поля пиши так, как они видны в меню игры (на языке игры, если неизвестен — по-английски). Разрешение и апскейлер указывай под монитор пользователя. Если прислан current — это уже разобранные пункты файла: бери из него name и current для этих пунктов.
        4. Для каждого пункта: current — текущее значение, разобранное из присланного файла и переведённое в подпись меню (например ключ 3 → "High"); если файла нет или пункт в нём не хранится — null. recommended — рекомендуемая подпись меню.
        5. Если пункт хранится в присланном файле, заполни section (секция INI или null), key (точное имя ключа из файла; для INI допустим документированный для движка ключ, которого в файле нет) и value — сырое значение для записи в файл в том виде, в каком оно там пишется (число, true/false, строка без лишних кавычек). Иначе section/key/value — null. Ключи не выдумывай.
        6. Исправь совет по последней просьбе: меняй только то, о чём просят или что из этого прямо следует, остальные пункты верни без изменений.
        7. Если прислан скриншот или файл — опирайся на него (меню игры, оверлей с FPS, лог).
        8. reply — 1–3 предложения по-русски: что поменял и почему, или почему не стал менять.
        9. summary, reason и notes — строго по-русски. summary — 2–3 предложения о том, что упирается в железо и чем пожертвовали. notes — предупреждения (например, что часть настроек надо выставить руками), иначе null.
        Отвечай только одним JSON-объектом по схеме, без пояснений и без markdown-ограждений:
        {"type":"object","additionalProperties":false,"required":["reply","summary","settings","notes"],"properties":{"reply":{"type":"string"},"summary":{"type":"string"},"settings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","current","recommended","section","key","value","reason"],"properties":{"name":{"type":"string"},"current":{"type":["string","null"]},"recommended":{"type":"string"},"section":{"type":["string","null"]},"key":{"type":["string","null"]},"value":{"type":["string","null"]},"reason":{"type":"string"}}}},"notes":{"type":["string","null"]}}}
        """;

    const string ReadPrompt = """
        Ты знаешь графические настройки ПК-игр. Тебе дают игру и её файл настроек с содержимым.
        Правила:
        1. settings — пункты графического меню игры, которые хранятся в этом файле, в порядке, в каком они обычно идут в меню. name — подпись пункта так, как она видна в меню игры (на языке игры, если неизвестен — по-английски).
        2. current — текущее значение, переведённое в подпись меню (например ключ 3 → "High"); если значение не удаётся понять — сырое значение из файла.
        3. section — секция INI или null, key — точное имя ключа из файла. Ключи не выдумывай; пункты, которых нет в файле, не включай.
        4. Служебные ключи (версии, идентификаторы, пути, статистику) не включай.
        Отвечай только одним JSON-объектом по схеме, без пояснений и без markdown-ограждений:
        {"type":"object","additionalProperties":false,"required":["settings"],"properties":{"settings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","current","section","key"],"properties":{"name":{"type":"string"},"current":{"type":["string","null"]},"section":{"type":["string","null"]},"key":{"type":["string","null"]}}}}}}
        """;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<ConfigLocation> LocateAsync(LocateRequest request, CancellationToken ct) =>
        Parse<ConfigLocation>(await Post(BuildLocateBody(request, ModelFor(choice?.Locate), EffortFor(choice?.Locate, LocateEffort)), ct));

    public async Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct) =>
        Parse<AdviceResult>(await Post(BuildAskBody(request, ModelFor(choice?.Ask), EffortFor(choice?.Ask, AskEffort)), ct));

    public async Task<AdjustResult> AdjustAsync(AdjustRequest request, CancellationToken ct) =>
        Parse<AdjustResult>(await Post(BuildAdjustBody(request, ModelFor(choice?.Ask), EffortFor(choice?.Ask, AskEffort)), ct));

    public async Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct)
    {
        var result = Parse<ReadResult>(await Post(BuildReadBody(request, ModelFor(choice?.Read), EffortFor(choice?.Read, ReadEffort)), ct));
        return result.Settings == null ? new ReadResult([]) : result;
    }

    static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    string ModelFor(ClaudeTask? task) => Trimmed(task?.Model) ?? Trimmed(choice?.Model) ?? Model;

    static string EffortFor(ClaudeTask? task, string builtIn) => Trimmed(task?.Effort) ?? builtIn;

    async Task<string> Post(string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/')) + "/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
        return text;
    }

    static object GameInfo(Game g) => new { name = g.Name, source = g.Source, installDir = g.InstallDir, exe = g.MainExe };

    static object[] Single(object user) => [new { role = "user", content = JsonSerializer.Serialize(user, Json) }];

    internal static string BuildLocateBody(LocateRequest r, string model, string effort = LocateEffort) => Body(model, 1000, 2, effort, LocatePrompt, Single(new
    {
        game = GameInfo(r.Game),
        installRootEntries = r.InstallRootEntries,
        candidates = r.CandidatePaths,
        unrealProject = r.UnrealProject,
    }));

    static Dictionary<string, object?> AskContext(AdviceRequest r) => new()
    {
        ["game"] = GameInfo(r.Game),
        ["hardware"] = r.Hardware,
        ["goal"] = new { mode = GoalTargets.Mode(r.Goal), fps = GoalTargets.For(r.Goal, r.Hardware.Hz).Fps },
        ["config"] = r.ConfigPath is null ? null : new { path = r.ConfigPath, format = r.ConfigFormat, text = r.ConfigText, truncated = r.Truncated },
        ["current"] = r.Current,
    };

    internal static string BuildAskBody(AdviceRequest r, string model, string effort = AskEffort) =>
        Body(model, 8000, 3, effort, AskPrompt, Single(AskContext(r)));

    internal static string BuildAdjustBody(AdjustRequest r, string model, string effort = AskEffort)
    {
        static object Text(string t) => new { type = "text", text = t };
        var history = r.History.TakeLast(MaxHistoryTurns).ToList();
        if (history.Count > 0 && history[0].Role == ChatRole.Assistant) history.RemoveAt(0);

        var msgs = history.Select(t => new
        {
            role = t.Role == ChatRole.User ? "user" : "assistant",
            content = new List<object> { Text(t.Text + (t.Attachments?.Count > 0 ? $"\n[вложения: {string.Join(", ", t.Attachments)}]" : "")) },
        }).ToList();
        var last = new List<object>();
        if (!string.IsNullOrEmpty(r.Text)) last.Add(Text(r.Text));
        last.AddRange(r.Attachments.Select(a => a.Block));
        msgs.Add(new { role = "user", content = last });

        var context = AskContext(r.Context);
        context["advice"] = r.Advice;
        msgs[0].content.Insert(0, Text(JsonSerializer.Serialize(context, Json)));
        return Body(model, 8000, 2, effort, AdjustPrompt, msgs);
    }

    internal static string BuildReadBody(ReadRequest r, string model, string effort = ReadEffort) => Body(model, 4000, 0, effort, ReadPrompt, Single(new
    {
        game = GameInfo(r.Game),
        config = new { path = r.ConfigPath, format = r.ConfigFormat, text = r.ConfigText, truncated = r.Truncated },
    }));

    static string Body(string model, int maxTokens, int maxSearches, string effort, string system, object messages)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["output_config"] = new { effort },
            ["system"] = system,
        };
        if (maxSearches > 0) body["tools"] = new[] { new { type = "web_search_20260209", name = "web_search", max_uses = maxSearches } };
        body["fallbacks"] = "default";
        body["messages"] = messages;
        return JsonSerializer.Serialize(body, Json);
    }

    internal static T Parse<T>(string responseJson)
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
            return JsonSerializer.Deserialize<T>(answer, Json) ?? throw new AdviceException(garbled);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AdviceException(garbled);
        }
    }
}
