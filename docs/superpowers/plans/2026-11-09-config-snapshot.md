# Текущие настройки после поиска конфига — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** сразу после «Найти конфиг» показывать текущие настройки игры подписями меню (запрос «Разобрать» + локальная таблица Unreal `sg.*`), хранить снимок и переиспользовать его в подборе.

**Architecture:** чистые модули `UnrealScalability` и `AdviceTable`; новый запрос `ClaudeClient.ReadAsync` без веб-поиска; `Advisor` хранит `GameOverride.Snapshot` с временем записи файла и обновляет его при поиске, подборе, применении и восстановлении; UI показывает таблицу снимка до совета.

**Tech Stack:** .NET 8, WPF, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-09-config-snapshot-design.md`

## Global Constraints

- Модели: `CurrentSetting(string Name, string? Current, string? Section, string? Key)`, `ConfigSnapshot(List<CurrentSetting> Settings, DateTime At, DateTime FileStamp)`; `GameOverride.Snapshot : ConfigSnapshot?`.
- `FileStamp` = `File.GetLastWriteTimeUtc(path)`. Снимок свежий ⇔ найденный путь существует и его время записи == `FileStamp`.
- Запрос «Разобрать»: `max_tokens` 4000, `output_config.effort = "low"`, без поля `tools`. Подбор и поиск без изменений (`medium`, `web_search` 3 и 2).
- Файл модели — тот же `ReadConfig` (до 32 К символов, `ConfigFinder.MaskSecrets`).
- Unreal: секция `ScalabilityGroups` (без учёта регистра), уровни 0–4 → `Low`, `Medium`, `High`, `Epic`, `Cinematic`, иначе сырое значение; `sg.ResolutionQuality` → `"{N}%"`. Ключи и подписи — таблица спеки (11 строк), порядок таблицы.
- Тексты UI: этап «Разбираем конфиг…»; статус ошибки «Не удалось разобрать конфиг: <сообщение>»; ссылка «Конфиг изменился — Обновить»; заголовок таблицы без совета «Текущие настройки:»; «Рекомендуется» без совета — «—».
- Тесты в `tests/.../Advice` — namespace `MetGameHUB.Tests`. Сторонних пакетов нет. Комментарии только для неочевидного.

## Review Focus

1. Снимок переживает перезапуск: `FileStamp` после `Library.Save`/`Load` сравнивается точно — тест `Snapshot_RoundTrip_StaysFresh` в Task 3.
2. Файл конфига удалён после разбора: снимок не свежий, ссылка «Обновить» не показывается (файла нет) — тест `IsSnapshotFresh_MissingFile_False` в Task 3, ссылка — `SnapshotStale` требует существующий файл (Task 4).
3. Разбор упал во время «Подобрать»: подбор всё равно выполняется, статус показывает ошибку — ручная проверка в Task 4.
4. Unreal-значение не число или вне 0–4 («abc», «7») показывается как есть — тест `Describe_UnknownValues_Raw` в Task 1.
5. «Найти заново» нашёл тот же путь: снимок сохраняется (сброс только при другом пути) — тест `Locate_SamePath_KeepsSnapshot` в Task 3.

---

### Task 1: Модели и `UnrealScalability`

**Files:**
- Modify: `src/metGameHUB/Advice/Models.cs` (после `SettingAdvice`)
- Create: `src/metGameHUB/Advice/UnrealScalability.cs`
- Test: `tests/metGameHUB.Tests/Advice/UnrealScalabilityTests.cs`

**Interfaces:**
- Produces: `CurrentSetting`, `ConfigSnapshot` (Global Constraints); `static class UnrealScalability` с
  - `CurrentSetting? Describe(string? section, string key, string rawValue)`
  - `List<CurrentSetting> FromFile(string text)` — через `IniEditor.CurrentValue(text, "ScalabilityGroups", key)`
  - `List<CurrentSetting> Merge(List<CurrentSetting> fromModel, string fileText)` — у строк модели с `Describe != null` заменяет `Current` локальным (имя модели остаётся), затем дописывает строки `FromFile`, чьих ключей нет в ответе модели (сравнение `(Section, Key)` без учёта регистра).

- [ ] **Step 1: Тесты**

```csharp
[Theory]
[InlineData("0", "Low")] [InlineData("2", "High")] [InlineData("4", "Cinematic")]
public void Describe_Levels(string raw, string label) =>
    Assert.Equal(new CurrentSetting("Тени", label, "ScalabilityGroups", "sg.ShadowQuality"),
        UnrealScalability.Describe("scalabilitygroups", "sg.ShadowQuality", raw));

[Theory]
[InlineData("abc")] [InlineData("7")]
public void Describe_UnknownValues_Raw(string raw) =>
    Assert.Equal(raw, UnrealScalability.Describe("ScalabilityGroups", "sg.TextureQuality", raw)!.Current);

[Fact]
public void Describe_ResolutionAndForeignKeys()
{
    Assert.Equal("100%", UnrealScalability.Describe("ScalabilityGroups", "sg.ResolutionQuality", "100.000000")!.Current);
    Assert.Equal("Масштаб разрешения", UnrealScalability.Describe("ScalabilityGroups", "sg.ResolutionQuality", "75")!.Name);
    Assert.Null(UnrealScalability.Describe("Other", "sg.ShadowQuality", "3"));
    Assert.Null(UnrealScalability.Describe(null, "sg.ShadowQuality", "3"));
    Assert.Null(UnrealScalability.Describe("ScalabilityGroups", "sg.Unknown", "3"));
}

[Fact]
public void FromFile_TableOrder()
{
    var text = "[ScalabilityGroups]\r\nsg.TextureQuality=3\r\nsg.ResolutionQuality=100.000000\r\nsg.ShadowQuality=1\r\n";
    Assert.Equal(["Масштаб разрешения", "Тени", "Текстуры"], UnrealScalability.FromFile(text).Select(s => s.Name));
}

[Fact]
public void Merge_OverridesCurrentAndAppendsMissing()
{
    var text = "[ScalabilityGroups]\nsg.ShadowQuality=1\nsg.TextureQuality=3\n";
    List<CurrentSetting> model = [new("Shadows", "Ultra", "ScalabilityGroups", "sg.ShadowQuality"), new("VSync", "On", "S", "bUseVSync")];
    var merged = UnrealScalability.Merge(model, text);
    Assert.Equal(new CurrentSetting("Shadows", "Medium", "ScalabilityGroups", "sg.ShadowQuality"), merged[0]);
    Assert.Equal("On", merged[1].Current);
    Assert.Equal(new CurrentSetting("Текстуры", "Epic", "ScalabilityGroups", "sg.TextureQuality"), merged[2]);
}
```

- [ ] **Step 2:** `dotnet test --filter UnrealScalabilityTests` — не компилируется.
- [ ] **Step 3:** Реализовать записи и класс (словарь ключ → подпись в порядке таблицы спеки; `ResolutionQuality` — `double.TryParse` с `CultureInfo.InvariantCulture`, `Math.Round`).
- [ ] **Step 4:** Тесты зелёные, затем `dotnet test` целиком.
- [ ] **Step 5:** Коммит `Add config snapshot models and Unreal scalability table`.

---

### Task 2: Запрос «Разобрать» в `ClaudeClient`

**Files:**
- Modify: `src/metGameHUB/Advice/ClaudeClient.cs` (записи запроса рядом с `AdviceRequest:11`; `AskPrompt:31-41`; `Body:98-107`; новые `ReadPrompt`, `ReadAsync`, `BuildReadBody`)
- Test: `tests/metGameHUB.Tests/Advice/ClaudeClientTests.cs`

**Interfaces:**
- Consumes: `CurrentSetting` (Task 1).
- Produces:
  - `public sealed record ReadRequest(Game Game, string ConfigPath, string ConfigFormat, string ConfigText, bool Truncated);`
  - `public sealed record ReadResult(List<CurrentSetting> Settings);`
  - `public async Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct)`
  - `internal static string BuildReadBody(ReadRequest r, string model)`
  - `AdviceRequest` получает последний параметр `List<CurrentSetting>? Current = null`; `BuildAskBody` кладёт `current = r.Current` в user.

- [ ] **Step 1: Тесты**

```csharp
[Fact]
public void BuildReadBody_NoToolsLowEffortWithConfig()
{
    var root = JsonDocument.Parse(ClaudeClient.BuildReadBody(new(new Game("G", @"C:\g", GameSource.Steam, "g.exe"), @"C:\g\a.ini", "ini", "k=1", false), "m")).RootElement;
    Assert.False(root.TryGetProperty("tools", out _));
    Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
    Assert.Equal(4000, root.GetProperty("max_tokens").GetInt32());
    var user = JsonDocument.Parse(root.GetProperty("messages")[0].GetProperty("content").GetString()!).RootElement;
    Assert.Equal("k=1", user.GetProperty("config").GetProperty("text").GetString());
}

[Fact]
public async Task ReadAsync_ParsesSettings()
{
    // ответ модели: {"settings":[{"name":"Shadows","current":"High","section":"S","key":"k"}]} через существующий хелпер Response(text: …)
    var result = await new ClaudeClient(new HttpClient(fake), "key").ReadAsync(req, CancellationToken.None);
    Assert.Equal(new CurrentSetting("Shadows", "High", "S", "k"), result.Settings.Single());
}

[Fact]
public void BuildAskBody_CurrentPassedOrNull()
{
    // Request() with { Current = [new("Shadows", "High", "S", "k")] } → user.current[0].name == "Shadows"
    // Request() (Current null) → user.current.ValueKind == Null
}
```

  Существующие проверки `effort == "medium"` и `max_uses` у поиска и подбора не меняются и должны остаться зелёными.
- [ ] **Step 2:** `dotnet test --filter ClaudeClientTests` — не компилируется.
- [ ] **Step 3:** `Body(string model, int maxTokens, int maxSearches, string effort, string system, object user)`; при `maxSearches == 0` поле `tools` в JSON отсутствует. Поиск и подбор передают `"medium"`, разбор — `Body(model, 4000, 0, "low", ReadPrompt, new { game = GameInfo(r.Game), config = new { path, format, text, truncated } })`. `ReadPrompt` — дословно из спеки. В `AskPrompt` в конец правила 3 дописать: «Если прислан current — это уже разобранные пункты файла: бери из него name и current для этих пунктов.»; во вводной после «файл настроек игры с его содержимым» добавить «и, если есть, уже разобранные текущие значения (current)».
- [ ] **Step 4:** `dotnet test` — всё зелёное.
- [ ] **Step 5:** Коммит `Add config read request to ClaudeClient`.

---

### Task 3: Снимок в `Advisor`

**Files:**
- Modify: `src/metGameHUB/Scan/Game.cs:20-26` (`GameOverride.Snapshot`)
- Modify: `src/metGameHUB/Advice/Advisor.cs` (`LocateAsync:19-45`, `AskAsync:47-59`, `Apply`, `Restore`; новые `ReadAsync`, `IsSnapshotFresh`)
- Test: `tests/metGameHUB.Tests/Advice/AdvisorTests.cs`

**Interfaces:**
- Consumes: Task 1 (`UnrealScalability.Describe/Merge`), Task 2 (`ReadRequest`, `ReadResult`, `ClaudeClient.ReadAsync`, `AdviceRequest.Current`).
- Produces:
  - `public async Task<ConfigSnapshot?> ReadAsync(Game game, CancellationToken ct)` — null без найденного существующего файла (запрос не уходит); иначе `Merge` ответа с текстом файла, `Snapshot = new(settings, DateTime.UtcNow, File.GetLastWriteTimeUtc(path))`, `lib.Save`.
  - `public static bool IsSnapshotFresh(Library lib, FinderRoots roots, Game game)`.
  - `LocateAsync`: если новый путь ≠ прежнему `ConfigPath` (без учёта регистра, null считается отличным) — `Snapshot = null`.
  - `AskAsync`: `Current` запроса = `Snapshot.Settings`, если свежий, иначе null; после ответа при существующем файле — `Snapshot` = `UnrealScalability.Merge(строки ответа с Key != null → CurrentSetting(Name, Current, Section, Key), текст файла)`, `FileStamp` текущий. (`Value` ответа — рекомендуемое значение, для текущего его не брать.)
  - `Apply`: если снимок есть — у строк с совпавшими `(Section, Key)` `Current = Recommended`, `FileStamp` новый.
  - `Restore`: если снимок есть — `Current` строк с ключом из `IniEditor.CurrentValue` восстановленного файла (через `Describe`, иначе сырое), `FileStamp` новый.

- [ ] **Step 1: Тесты** (окружение `Env` из файла; ответ разбора — хелпер, аналогичный `Reply`, возвращающий `{"settings":[{"name":"A","current":"1","section":"S","key":"A"}]}`)

```csharp
[Fact] public async Task ReadAsync_StoresSnapshotWithStamp()   // Snapshot.Settings[0].Name == "A"; FileStamp == File.GetLastWriteTimeUtc(e.Ini); сохранено в LibPath
[Fact] public async Task ReadAsync_NoFile_ReturnsNullWithoutRequest()   // ConfigPath = несуществующий → null, e.Handler.Body == null
[Fact] public async Task ReadAsync_SendsMaskedText()   // "Password=hunter2" в файле → в теле "Password=***", нет "hunter2"
[Fact] public void IsSnapshotFresh_ChangedStamp_False()   // File.SetLastWriteTimeUtc(e.Ini, +1 мин) → false
[Fact] public void IsSnapshotFresh_MissingFile_False()
[Fact] public void Snapshot_RoundTrip_StaysFresh()   // Snapshot со stamp файла → Library.Save/Load → IsSnapshotFresh(loaded) == true
[Fact] public async Task Locate_OtherPath_ResetsSnapshot()
[Fact] public async Task Locate_SamePath_KeepsSnapshot()
[Fact] public async Task AskAsync_SendsCurrentOnlyWhenFresh()   // свежий → user.current не null; после SetLastWriteTimeUtc → null
[Fact] public async Task AskAsync_UpdatesSnapshotFromAnswer()   // ответ подбора со строкой Key "A" → Snapshot.Settings[0].Current == current из ответа, свежий
[Fact] public void Apply_And_Restore_KeepSnapshotFresh()   // после Apply: Current строки "A" == "Rec", свежий; после Restore: Current == "1", свежий
```

- [ ] **Step 2:** `dotnet test --filter AdvisorTests` — не компилируется / красные.
- [ ] **Step 3:** Реализовать по Interfaces.
- [ ] **Step 4:** `dotnet test` — всё зелёное.
- [ ] **Step 5:** Коммит `Store config snapshot in Advisor`.

---

### Task 4: Таблица, страница игры, документы

**Files:**
- Create: `src/metGameHUB/Ui/AdviceTable.cs`
- Modify: `src/metGameHUB/Ui/GameRow.cs:11` (`AdviceRowVm` переезжает в `AdviceTable.cs` без изменений), `GameRow.cs:283,290-308` (`RefreshCanApply`, `RefreshConfig`, `BuildRows`)
- Modify: `src/metGameHUB/Ui/MainWindow.xaml:240-300` (строка конфига, заголовок и видимость таблицы), `src/metGameHUB/Ui/MainWindow.xaml.cs` (`Locate_Click`, `RunAdviceAsync`, новый обработчик ссылки)
- Modify: `CLAUDE.md`, `README.md`, `docs/backlog.md` (удалить «Текущие настройки сразу после поиска конфига» и «Таблица качества Unreal»)
- Test: `tests/metGameHUB.Tests/Ui/AdviceTableTests.cs`

**Interfaces:**
- Consumes: Task 1 (`CurrentSetting`, `ConfigSnapshot`, `UnrealScalability.FromFile`), Task 3 (`Advisor.ReadAsync`, `Advisor.IsSnapshotFresh`, `GameOverride.Snapshot`, `LocateAsync` → `ConfigStatus(Path, Exists, Format, Note)`).
- Produces:
  - `public static class AdviceTable { public static List<AdviceRowVm> Build(Advice.Advice? advice, IReadOnlyList<CurrentSetting>? current) }` — без совета: строки `current` → `AdviceRowVm(Name, Current ?? "—", "—", "", Key != null)`; с советом: строки совета, `Current` = совпадение в `current` по `(Section, Key)` без учёта регистра ?? `s.Current` ?? «—».
  - `GameRow`: `bool HasRows`, `bool SnapshotStale` (найденный файл существует, снимок есть и не свежий), `void ShowProvisional(IReadOnlyList<CurrentSetting> rows)`; в `BuildRows`: `current` = совет есть ? (свежий ? `Snapshot.Settings` : null) : (`Snapshot?.Settings` ?? временные строки); `AdviceHeading` без совета — «Текущие настройки:».

- [ ] **Step 1: Тесты `AdviceTable`**

```csharp
[Fact] public void Build_NoAdvice_RowsFromCurrentWithDash()
    // [new("Тени","Medium","ScalabilityGroups","sg.ShadowQuality")] → AdviceRowVm("Тени","Medium","—","",true)
[Fact] public void Build_Advice_CurrentByKeyCaseInsensitive()
    // совет: SettingAdvice("Shadows","Old","Low","S","k","0","r"); current: ("x","New","s","K") → Current == "New"
[Fact] public void Build_Advice_NoMatchOrNoCurrent_UsesAdvice()
    // current null → "Old"; строка без ключа → её Current или "—"
```

- [ ] **Step 2:** `dotnet test --filter AdviceTableTests` — не компилируется.
- [ ] **Step 3:** `AdviceTable.Build` и перенос `AdviceRowVm`.
- [ ] **Step 4: `GameRow`.** `BuildRows` → `AdviceTable.Build`; `HasRows`, `SnapshotStale`, `ShowProvisional` (временные строки сбрасываются в `LoadAdvice`/после разбора); `RefreshConfig` уведомляет `SnapshotStale`, `HasRows`, `AdviceRows`, `AdviceHeading`.
- [ ] **Step 5: XAML.** Таблица (заголовок колонок и `ItemsControl`) видима по `HasRows`, `AdviceSummary` и `AdviceNotes` — по `HasAdvice` как раньше; подсказка «Нажмите «Подобрать»…» остаётся при `!HasAdvice`. Рядом с «Найти заново»: `Button Content="Конфиг изменился — Обновить" Style="{StaticResource LinkButton}" Click="RefreshSnapshot_Click" IsEnabled="{Binding IsIdle}" Visibility="{Binding SnapshotStale, Converter={StaticResource BoolToVisibility}}"`.
- [ ] **Step 6: `MainWindow`.** Режимы запроса `Locate`, `Ask`, `Read` (вместо `bool ask`). После поиска: если `status.Exists` и `status.Format != "none"` — `ShowProvisional(UnrealScalability.FromFile(File.ReadAllText(path)))` (ошибки чтения файла игнорировать), этап «Разбираем конфиг…», `await advisor.ReadAsync`; исключение разбора (кроме отмены) → статус «Не удалось разобрать конфиг: …», в режиме `Ask` подбор продолжается. `Ask` при уже найденном конфиге — сразу подбор. `RefreshSnapshot_Click` → режим `Read`. После каждого шага `RefreshConfig`/`RefreshCanApply` у `Targets(row)`.
- [ ] **Step 7:** `dotnet build` (0 предупреждений), `dotnet test` — зелёные.
- [ ] **Step 8: Ручная проверка** (контролёр/пользователь): «Найти заново» у Unreal-игры — строки `sg.*` появляются сразу, затем таблица от модели; у игры без совета колонка «Рекомендуется» — «—»; изменить файл конфига руками → появляется «Конфиг изменился — Обновить», после нажатия ссылка исчезает; «Применить» не вызывает ссылку; разбор с неверным ключом API во время «Подобрать» показывает ошибку разбора (и ошибку подбора).
- [ ] **Step 9: Документы.** `CLAUDE.md`: в «Данные» — `Snapshot` в `library.json`; пункт про советчик — три запроса (поиск, разбор: без веб-поиска, effort low, 4000 токенов; подбор), правило свежести по времени записи, сброс при другом пути, обновление при подборе/применении/восстановлении; таблица Unreal `sg.*`. `README.md` — страница игры показывает текущие настройки после поиска. `docs/backlog.md` — удалить два пункта.
- [ ] **Step 10:** Коммит `Show current config settings right after locating the config`.
