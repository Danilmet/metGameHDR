# Советчик по графическим настройкам — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** На игру задаётся цель (FPS + приоритет), приложение спрашивает Claude с веб-поиском и содержимым найденных конфигов, показывает рекомендации и по кнопке применяет правки в текстовый конфиг с подтверждением.

**Architecture:** Новая папка `Advice/` с пятью независимыми единицами (железо, поиск конфигов, редактор INI, HTTP-клиент Claude, склейка `Advisor`), данные на игру в `GameOverride`, глобальные в `Settings`, интерфейс — секция в панели деталей и карточка в настройках `MainWindow`, окно диффа. Сетевые вызовы и файловые операции тестируются через фейки и временные папки.

**Tech Stack:** .NET 8, WPF, System.Text.Json, HttpClient, P/Invoke user32; xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-graphics-advisor-design.md`

## Global Constraints

- Сторонних NuGet-пакетов нет (кроме xUnit в тестах). Без COM, без прав администратора.
- Все строки интерфейса на русском. Комментарии только там, где поведение неочевидно.
- Стиль тестов: класс `XxxTests`, метод `Method_Condition_Result`, фейки — вложенные `sealed class`, временные файлы через `Path.GetTempPath()+Guid` с очисткой в `finally`.
- P/Invoke: `[DllImport("user32.dll")] static extern ...`, приватные, как в `Watch/ProcessList.cs`.
- Nullable и ImplicitUsings включены; `System.IO` и `System.Net.Http` подключать явно (см. `Settings.cs:1`, `Ui/MainWindow.xaml.cs:4`).
- Claude API: модель `claude-opus-5-5`, заголовки `x-api-key`, `anthropic-version: 2026-10-21`, `anthropic-beta: server-side-fallback-2026-07-01`; инструмент `web_search_20260209` с `max_uses: 5`; `fallbacks: "default"`; `output_config.format` — JSON-схема из задачи 5. Форму поля `format` исполнитель сверяет с https://platform.claude.com/docs/en/build-with-claude/structured-outputs перед кодом.
- Один коммит на задачу; `dotnet test` зелёный перед каждым коммитом.

## Review Focus

1. Путь конфига из ответа модели записан с другим регистром или прямыми слэшами — применение всё равно должно сопоставить его с кандидатом (тест в задаче 6).
2. Unreal-конфиг с ключами-массивами `+CustomSettings=...` — правка ключа `CustomSettings` не должна трогать строки с `+` (тест в задаче 4).
3. Конфиг в UTF-16 с BOM — после записи BOM и кодировка те же, байты вне правок совпадают (тест в задаче 4).
4. В корне папки игры тысячи файлов — ищем только `*.ini`/`*.cfg` первого уровня и прекращаем после лимита 100 КБ (тест в задаче 3).
5. HTTP 429/529 или `stop_reason: max_tokens` — пользователь видит понятный статус, а не исключение (тест в задаче 5).

---

### Task 1: Данные — цель и рекомендация на игру, ключ и цель по умолчанию

**Files:**
- Modify: `src/metGameHDR/Scan/Game.cs:13-18` (GameOverride)
- Create: `src/metGameHDR/Advice/Models.cs`
- Modify: `src/metGameHDR/Settings.cs:10-15`
- Test: `tests/metGameHDR.Tests/Scan/LibraryTests.cs`, `tests/metGameHDR.Tests/SettingsTests.cs`

**Interfaces (Produces):**
```csharp
namespace MetGameHDR.Advice;
public enum Priority { Quality, Balanced, Responsive }
public sealed record GraphicsGoal(int Fps, Priority Priority);
public sealed record SettingAdvice(string Name, string Value, string Reason);
public sealed record ConfigEdit(string? Section, string Key, string Value);
public sealed record ConfigAdvice(string Path, List<ConfigEdit> Edits);
public sealed record AdviceResult(string Summary, List<SettingAdvice> Settings, ConfigAdvice? Config, string? Notes);
public sealed record Advice(AdviceResult Result, DateTime At);
// GameOverride: + public GraphicsGoal? Goal { get; set; }  + public Advice? Advice { get; set; }
// Settings:     + public string? ClaudeApiKey { get; set; } + public int? DefaultFps { get; set; } + public Priority DefaultPriority { get; set; } = Priority.Balanced;
```
Enum сериализуется строкой: `[JsonConverter(typeof(JsonStringEnumConverter))]` на `Priority`.

- [ ] Тест `Save_Load_RoundTripsGoalAndAdvice` в LibraryTests: override с Goal(120, Responsive) и Advice с одной настройкой и Config — после Load равны.
- [ ] Тест `Load_OldFileWithoutAdviceFields_ReadsDefaults`: JSON с одним `HdrEnabled` → Goal и Advice null.
- [ ] Тест `Save_Load_RoundTripsClaudeFields` в SettingsTests: ключ, DefaultFps=90, DefaultPriority=Quality; и что `"DefaultPriority":"Quality"` — строка в файле.
- [ ] Убедиться, что тесты падают (нет типов).
- [ ] Добавить записи и поля.
- [ ] `dotnet test` зелёный, коммит `Add per-game graphics goal and advice fields`.

### Task 2: Железо

**Files:**
- Create: `src/metGameHDR/Advice/Hardware.cs`
- Test: `tests/metGameHDR.Tests/Advice/HardwareTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record HardwareInfo(string Gpu, int? VramGb, string Cpu, int RamGb, int Width, int Height, int Hz, string Os);
public static class Hardware
{
    public static HardwareInfo Detect();                       // никогда не бросает; неизвестное — "неизвестно" / null / 0
}
```
Источники: `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000..` — `DriverDesc`, `HardwareInformation.qwMemorySize` (QWORD; если это REG_BINARY — 8 байт little-endian), первый с ненулевой памятью; `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString`; ОЗУ — `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` округлённо до ГБ; монитор — `EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS=-1, ref DEVMODE)` → `dmPelsWidth/Height/dmDisplayFrequency`; ОС — `Environment.OSVersion.VersionString`.

- [ ] Тест `Detect_DoesNotThrow_AndFillsMonitor`: `Hz > 0`, `Width > 0`, `Gpu` и `Cpu` не пустые (на CI без дисплея допускается `Hz == 0` — пометить `[Fact(Skip=...)]` только если CI падает).
- [ ] Тест падает (нет класса), реализовать, зелёный.
- [ ] Коммит `Detect GPU, CPU, RAM and monitor mode for advice requests`.

### Task 3: Поиск кандидатов конфигов

**Files:**
- Create: `src/metGameHDR/Advice/ConfigFinder.cs`
- Test: `tests/metGameHDR.Tests/Advice/ConfigFinderTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record ConfigCandidate(string Path, string Text, bool Truncated);
public sealed record FinderRoots(string LocalAppData, string Documents, string SavedGames)
{
    public static FinderRoots Default();   // Environment.GetFolderPath + KnownFolder SavedGames = %USERPROFILE%\Saved Games
}
public static class ConfigFinder
{
    public const int MaxFiles = 3, MaxTotalBytes = 100 * 1024, MaxRootFileBytes = 64 * 1024;
    public static IReadOnlyList<ConfigCandidate> Find(Game game, FinderRoots roots);
    internal static string Normalize(string name);              // нижний регистр, только буквы и цифры
    internal static bool NameMatches(string folder, Game game); // Normalize(folder) == Normalize(Name | имя папки установки | exe без расширения)
}
```
Порядок обхода: Unreal в LocalAppData → корень папки игры (`*.ini`, `*.cfg` ≤ 64 КБ) → `Documents\My Games\<совп.>` и `SavedGames\<совп.>` (`*.ini|*.cfg|*.txt`, имя содержит settings|config|video|graphics|user). Остановка при `MaxFiles` или `MaxTotalBytes`; последний файл обрезается по началу с `Truncated = true`. Чтение с автоопределением BOM (`File.ReadAllText`).

- [ ] Тест `Find_UnrealLayout_ReturnsGameUserSettings`: временный LocalAppData `Foo\Saved\Config\WindowsNoEditor\GameUserSettings.ini`, игра Name «Foo: Bar» → один кандидат, Truncated false.
- [ ] Тест `NameMatches_IgnoresCaseAndPunctuation`: «Foo-Bar 2» ↔ папка «foobar2».
- [ ] Тест `Find_RootWithManyFiles_StopsAtLimits`: корень с 5 ini по 30 КБ → 3 файла, суммарно ≤ 100 КБ, последний Truncated true; файл 70 КБ пропущен.
- [ ] Тест `Find_NoMatches_ReturnsEmpty`.
- [ ] Падает → реализовать → зелёный → коммит `Find candidate config files for a game`.

### Task 4: Редактор текстового конфига

**Files:**
- Create: `src/metGameHDR/Advice/IniEditor.cs`
- Test: `tests/metGameHDR.Tests/Advice/IniEditorTests.cs`

**Interfaces (Produces):**
```csharp
public static class IniEditor
{
    public static string Apply(string text, IReadOnlyList<ConfigEdit> edits);       // перевод строк — по первому вхождению в text, иначе CRLF
    public static byte[] ApplyBytes(byte[] file, IReadOnlyList<ConfigEdit> edits);  // BOM/кодировка сохраняются: UTF-8 BOM, UTF-16 LE/BE BOM, иначе UTF-8 без BOM
    public static string? CurrentValue(string text, string? section, string key);   // null — ключа нет
}
```
Правила: секция `[Name]` без учёта регистра; `Section == null` — область до первой секции; ключ сравнивается без учёта регистра по части до разделителя (после `TrimStart`), строки с префиксом `+`, `-`, `.`, `!`, `;`, `#` ключом не считаются; разделитель — первый `=`, иначе первый пробел; при замене сохраняются ключ, разделитель и окружающие пробелы, меняется только остаток строки; новый ключ — `key=value` перед следующей строкой `[` (пустые строки хвоста секции остаются после вставки), новая секция — в конце файла после пустой строки.

- [ ] Тесты: `Apply_ExistingKey_ReplacesValueOnly` (`sg.ShadowQuality=3` → `=1`, остальные байты равны), `Apply_SpaceSeparator_KeepsSeparator` (`seta r_shadows "1"` → `"0"`), `Apply_MissingKey_AppendsToSection`, `Apply_MissingSection_AppendsSection`, `Apply_PlusPrefixedLine_IsNotMatched` (`+CustomSettings=` рядом с отсутствующим `CustomSettings` → дописан, `+`-строка нетронута), `Apply_PreservesCrlfAndComments`, `ApplyBytes_Utf16Bom_RoundTrips` (BOM FF FE на месте, остальное байт-в-байт вне правки), `ApplyBytes_Utf8Bom_Kept`, `CurrentValue_FirstMatchInSection`.
- [ ] Падает → реализовать → зелёный → коммит `Add line-preserving INI editor`.

### Task 5: Клиент Claude

**Files:**
- Create: `src/metGameHDR/Advice/ClaudeClient.cs`
- Test: `tests/metGameHDR.Tests/Advice/ClaudeClientTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record AdviceRequest(Game Game, HardwareInfo Hardware, GraphicsGoal Goal, IReadOnlyList<ConfigCandidate> Candidates);
public sealed class AdviceException(string message) : Exception(message);
public sealed class ClaudeClient(HttpClient http, string apiKey)
{
    public const string Model = "claude-opus-5-5";
    public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct);
    internal static string BuildBody(AdviceRequest request);   // JSON тела запроса
    internal static AdviceResult Parse(string responseJson);    // бросает AdviceException с русским текстом
}
```
Разбор ответа: `stop_reason == "refusal"` → «Модель отказалась отвечать»; `"max_tokens"` → «Ответ оборвался, повторите»; JSON берётся из последнего блока `content[].type == "text"` (перед ним идут `server_tool_use` и `web_search_tool_result`); невалидный JSON → «Модель вернула неразборчивый ответ». HTTP не 2xx → `AdviceException` с кодом и первыми 200 символами тела, для 429 и 529 текст «Сервис перегружен, повторите позже».

Тело запроса (приводится целиком, промпт — дословно):

```json
{
  "model": "claude-opus-5-5",
  "max_tokens": 8000,
  "system": "<SYSTEM>",
  "tools": [{ "type": "web_search_20260209", "name": "web_search", "max_uses": 5 }],
  "fallbacks": "default",
  "output_config": { "format": { "type": "json_schema", "schema": <SCHEMA> } },
  "messages": [{ "role": "user", "content": "<USER>" }]
}
```

`<SYSTEM>`:
```
Ты эксперт по оптимизации графических настроек ПК-игр. Тебе дают игру, железо пользователя, цель (кадры в секунду и приоритет: картинка, баланс или отзывчивость) и содержимое найденных файлов настроек игры.
Правила:
1. Найди свежие материалы по оптимизации именно этой игры (статьи, тесты, форумы) и опирайся на них и на известное поведение движка.
2. В settings перечисли настройки так, как они называются в меню игры, со значением и причиной в одну фразу. Разрешение и апскейлер указывай под монитор пользователя. Цель по кадрам — для типичных сцен, с запасом ~10%.
3. config заполняй только если один из присланных файлов действительно хранит графические настройки: path — точно как прислан; в edits используй только ключи, которые видишь в файле или которые документированы для этого движка. Ключи не выдумывай. Если подходящего файла нет — config: null.
4. Пиши по-русски, кратко. summary — 2–3 предложения о том, что упирается в железо и чем пожертвовали.
```

`<USER>` — JSON объекта `{ game: {name, source, installDir, exe}, hardware: {...HardwareInfo}, goal: {fps, priority}, candidates: [{path, text, truncated}] }`.

`<SCHEMA>`:
```json
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
```

- [ ] Тесты с фейковым `HttpMessageHandler` (как в `Steam/SteamGridDbTests.cs:11-24`): `AskAsync_SendsHeadersModelAndTools` (x-api-key, anthropic-version, beta, model, tools[0].type, output_config.format.type), `AskAsync_ValidResponse_ParsesResult` (content с `server_tool_use`, `web_search_tool_result`, `text` → Settings.Count и Config.Path), `Parse_Refusal_Throws`, `Parse_MaxTokens_Throws`, `Parse_InvalidJson_Throws`, `AskAsync_429_ThrowsOverloaded`.
- [ ] Падает → реализовать → зелёный → коммит `Add Claude client for graphics advice`.

### Task 6: Advisor — склейка и применение

**Files:**
- Create: `src/metGameHDR/Advice/Advisor.cs`
- Test: `tests/metGameHDR.Tests/Advice/AdvisorTests.cs`

**Interfaces (Consumes):** задачи 1–5. **Produces:**
```csharp
public sealed record EditPreview(ConfigEdit Edit, string? Current);
public enum ApplyOutcome { Applied, GameRunning, NoConfig, PathNotCandidate, FileMissing }
public sealed class Advisor(Func<string?> apiKey, Library lib, string libraryPath, Func<HardwareInfo> hardware, FinderRoots roots, Func<Game, bool> isRunning, HttpClient http)
{
    public Task<Advice> AskAsync(Game game, GraphicsGoal goal, CancellationToken ct);  // Detect → Find → Ask → lib.For(game).Advice = …, lib.Save
    public static bool IsCandidate(Game game, FinderRoots roots, string path);          // Path.GetFullPath + OrdinalIgnoreCase против ConfigFinder.Find(...).Select(c => c.Path)
    public IReadOnlyList<EditPreview> Preview(Game game, Advice advice);                  // CurrentValue по текущему файлу; пусто, если Config null
    public ApplyOutcome Apply(Game game, Advice advice);
}
```
`Apply`: порядок проверок — `Config == null` → NoConfig; `!IsCandidate` → PathNotCandidate; файла нет → FileMissing; `isRunning(game)` → GameRunning; иначе `.orig` (если нет), `.bak` (перезапись), `IniEditor.ApplyBytes`, запись через временный файл и `File.Move(overwrite)` (как `Settings.WriteAtomic`, но байты). `isRunning` в приложении: `ProcessList.Pids().Select(ProcessList.Path)` и `StartsWith(game.InstallDir + '\\', OrdinalIgnoreCase)`.

- [ ] Тесты (временная папка, фейковый handler отдаёт готовый ответ): `AskAsync_StoresAdviceOnOverrideAndSaves`, `Apply_GameRunning_ReturnsGameRunning_AndFileUntouched`, `Apply_PathWithDifferentCaseAndSlashes_StillApplies`, `Apply_PathOutsideCandidates_ReturnsPathNotCandidate`, `Apply_Twice_KeepsFirstOrigAndRewritesBak`, `Preview_ReturnsCurrentValues`.
- [ ] Падает → реализовать → зелёный → коммит `Add Advisor: ask Claude and apply config edits`.

### Task 7: Интерфейс и документация

**Files:**
- Modify: `src/metGameHDR/Ui/MainWindow.xaml:139-163` (панель деталей, после TextBox SearchName), `:207-213` (после карточки SteamGridDB — новая карточка «Claude»)
- Modify: `src/metGameHDR/Ui/MainWindow.xaml.cs` (поля `ClaudeKeyBox`, `DefaultFpsBox`, `DefaultPriorityBox` по образцу `KeyBox` :36, :48-52; обработчики `Ask_Click`, `Apply_Click`; создание `Advisor` рядом с `:160`)
- Modify: `src/metGameHDR/Ui/GameRow.cs` (свойства `GoalFps` (string, пусто = по умолчанию), `GoalPriority` (Priority), `AdviceSummary`, `AdviceItems` (IReadOnlyList<SettingAdvice>), `AdviceAt` (string), `CanApply`, `AdviceStatus`, `IsAsking`; запись Goal в override через `lib.For(Game)` и `Save()`)
- Create: `src/metGameHDR/Ui/ApplyDialog.xaml(.cs)` — `ApplyDialog(IReadOnlyList<EditPreview> items)`, ListView «Ключ | Сейчас | Станет», кнопки «Применить» (AccentButton) / «Отмена», `DialogResult`
- Modify: `CLAUDE.md` — структура (`Advice/`), данные (`Goal`/`Advice` в library.json, ключ Claude в settings.json), неочевидное (`.orig` один раз и `.bak` каждый раз; путь из ответа обязан совпасть с кандидатом; JSON берётся из последнего text-блока)

Секция «Графика» в панели деталей: заголовок, строка «FPS» (TextBox, ширина 60, водяной знак — частота монитора из `Hardware.Detect()` один раз при старте) и ComboBox приоритета («Картинка», «Баланс», «Отзывчивость»), кнопка «Подобрать» (неактивна во время запроса и без ключа; без ключа вместо кнопки TextBlock «Укажите ключ Claude в настройках»), `AdviceSummary`, ItemsControl по `AdviceItems` («Имя — Значение», причина вторым TextBlock стилем TextSecondary), `AdviceAt` мелко, кнопка «Применить» (`IsEnabled=CanApply`), `AdviceStatus`. Действующая цель считается в `MainWindow`: `lib.For(game).Goal ?? new GraphicsGoal(settings.DefaultFps ?? hardware.Hz, settings.DefaultPriority)`, где `hardware` — результат `Hardware.Detect()` при старте окна. `Ask_Click`: `IsAsking=true`, `await advisor.AskAsync(game, цель, ...)`, `catch (Exception e) when (e is AdviceException or HttpRequestException or TaskCanceledException)` → `AdviceStatus = e.Message`, `Log.Write`. `Apply_Click`: `Preview` → `ApplyDialog` → при `true` `Apply`, результат в `AdviceStatus` («Применено», «Закройте игру», «Файл не найден» …). Свой `HttpClient` для Claude: `static readonly HttpClient ClaudeHttp = new() { Timeout = TimeSpan.FromMinutes(3) }`.

- [ ] Карточка «Claude» в настройках и сохранение трёх полей в `Settings` (по образцу `KeyBox`).
- [ ] Свойства в `GameRow`, секция в XAML, `ApplyDialog`.
- [ ] Обработчики `Ask_Click`, `Apply_Click`; `dotnet build` без предупреждений по новым файлам.
- [ ] Обновить `CLAUDE.md`.
- [ ] Руками через терминал пользователя: запрос на одной Unreal-игре → «Применить» → проверить `.orig`, `.bak` и изменённые строки; запрос на игре без конфига → «Применить» неактивна; без ключа — подсказка.
- [ ] `dotnet test` зелёный, коммит `Add graphics advice section, Claude settings card and apply dialog`.
