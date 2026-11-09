# How Long to Beat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Строка «HLTB: сюжет · + доп. · 100%» в шапке страницы игры, данные с howlongtobeat.com, кэш в `library.json`.

**Architecture:** `Hltb/HltbClient` — HTTP (токен → поиск) плюс чистые статические функции: разбор, выбор совпадения, свежесть, форматирование. `HltbInfo` хранится в `GameOverride.Hltb`. `GameRow` отдаёт текст строки, `MainWindow` загружает данные при открытии страницы и при уходе фокуса из «Имени для поиска».

**Tech Stack:** .NET 8, WPF, `System.Text.Json`, `HttpClient`, xUnit. Сторонних пакетов не добавлять.

**Spec:** `docs/superpowers/specs/2026-11-09-how-long-to-beat-design.md` — рецепт запроса, тело POST, тексты UI и правила округления берутся оттуда дословно.

## Global Constraints

- Адреса: `GET https://howlongtobeat.com/api/search/site/init?t=<unix ms>` → `{"token":"..."}`; `POST https://howlongtobeat.com/api/search/site` с `x-auth-token`, `Referer: https://howlongtobeat.com/`, `Origin: https://howlongtobeat.com`, `Content-Type: application/json`; страница игры — `https://howlongtobeat.com/game/<id>`.
- Один константный браузерный `User-Agent` на init и search (к нему привязан токен), заголовки ставятся на каждый `HttpRequestMessage`, а не на общий `HttpClient` (он общий с обложками и Steam).
- Кэш: найдено — 30 дней, не найдено — 7 дней, сравнение `Query` с текущим `SearchNameOf`. Ошибки не кэшируются, а пишутся в `Log.Write`.
- Тексты: `HLTB: …`, `HLTB: сюжет X · + доп. Y · 100% Z`, `HLTB: нет данных`, `HLTB: не найдено`; часы — «45 мин», «2,5 ч», «3 ч», «26 ч» (десятичная запятая).
- Тесты — namespace `MetGameHUB.Tests` (не `MetGameHUB.Tests.Hltb`: см. CLAUDE.md про `Advice`). Комментарии в коде — только для неочевидного.

## Review Focus

1. Сайт отвечает 200, но без `data` (`{"error":"Session expired or invalid fingerprint"}`) → это ошибка, а не «не найдено»: иначе игра на 7 дней залипнет в «не найдено». Тест `Parse_ErrorBodyThrows` (Task 1).
2. Имя с `™®©` или двойными пробелами («Tom Clancy's Rainbow Six® Siege») → в `searchTerms` уходит без этих символов и без пустых элементов. Тест `SearchTerms_StripsMarksAndEmpty` (Task 1).
3. Токен протух (403) → один повторный init и поиск, а не ошибка; второй 403 → исключение. Тест `FetchAsync_RetriesOnceOn403` (Task 1).
4. Старый `library.json` без поля `Hltb` грузится, а `HltbInfo` переживает сохранение и загрузку (позиционный record). Тест `Hltb_RoundTripsAndMissingIsNull` (Task 1).
5. Переключение на другую игру во время запроса → ответ записывается в свою игру, повторный запрос для той же строки не стартует. Проверка вручную (Task 2, шаг проверки).

---

### Task 1: Модель и `HltbClient`

**Files:**
- Modify: `src/metGameHUB/Scan/Game.cs` — `HltbInfo` и `GameOverride.Hltb` (после `Chat`, строка ~29)
- Create: `src/metGameHUB/Hltb/HltbClient.cs`
- Test: `tests/metGameHUB.Tests/Hltb/HltbClientTests.cs`, `tests/metGameHUB.Tests/Scan/LibraryTests.cs` (добавить тест)

**Interfaces:**
- Produces:
  - `namespace MetGameHUB.Scan; public sealed record HltbInfo(int? Id, string? Name, int Main, int Plus, int Full, string Query, DateTime Fetched);` (время в секундах)
  - `GameOverride.Hltb : HltbInfo?` (`null` = не запрашивали)
  - `namespace MetGameHUB.Hltb; public sealed record HltbHit(int Id, string Name, string Type, int Main, int Plus, int Full);`
  - `public sealed class HltbClient(HttpClient http)`:
    - `Task<HltbInfo> FetchAsync(string name, DateTime now, CancellationToken ct = default)` — `Query = name`, `Fetched = now`; ничего не подошло → `Id = null`, `Name = null`, нули.
    - `static string[] SearchTerms(string name)`
    - `static List<HltbHit> Parse(string json)` — нет массива `data` → `JsonException`/`InvalidDataException`
    - `static HltbHit? Pick(IReadOnlyList<HltbHit> hits, string name)`
    - `static string Normalize(string s)`
    - `static bool IsFresh(HltbInfo? info, string query, DateTime now)`
    - `static string Format(HltbInfo info)` — полный текст строки, включая `HLTB: `
    - `static string GameUrl(int id)` → `https://howlongtobeat.com/game/{id}`

- [ ] **Step 1: Написать падающие тесты** в `HltbClientTests.cs` (fake `HttpMessageHandler` — по образцу `Steam/SteamGridDbTests.cs`, отвечает по суффиксу URL и считает вызовы init):
  - `Parse_ReadsSecondsAndMissingAsZero`: `{"data":[{"game_id":2127,"game_name":"Cyberpunk 2077","game_type":"game","comp_main":94172,"comp_plus":227747,"comp_100":null}]}` → один hit `(2127,"Cyberpunk 2077","game",94172,227747,0)`; hit без поля `comp_plus` → `Plus == 0`.
  - `Parse_ErrorBodyThrows`: `{"error":"Session expired or invalid fingerprint"}` → `Assert.ThrowsAny<Exception>`.
  - `SearchTerms_StripsMarksAndEmpty`: `"Tom Clancy's  Rainbow Six® Siege"` → `["Tom","Clancy's","Rainbow","Six","Siege"]`.
  - `Pick_PrefersExactNormalizedName`: hits `[("Cyberpunk 2077: Phantom Liberty", game), ("Cyberpunk 2077", game)]`, имя `"Cyberpunk 2077™"` → второй.
  - `Pick_FallsBackToFirstGame`: нет точного → первый с `Type == "game"`.
  - `Pick_SkipsDlc`: `[("Cyberpunk 2077","dlc"),("Cyberpunk 2077 Ultimate","game")]`, имя `"Cyberpunk 2077"` → `"Cyberpunk 2077 Ultimate"`.
  - `Pick_EmptyReturnsNull`: `[]` и список из одних DLC → `null`.
  - `IsFresh_QueryChangedIsStale`: `info.Query = "A"`, query `"B"` → `false`; `null` → `false`.
  - `IsFresh_FoundExpiresAfter30Days`: `Id = 1`, 29 дней → `true`, 31 день → `false`.
  - `IsFresh_NotFoundExpiresAfter7Days`: `Id = null`, 6 дней → `true`, 8 дней → `false`.
  - `Format_HoursRounding`: `(1,"X",2700,9000,94172)` → `"HLTB: сюжет 45 мин · + доп. 2,5 ч · 100% 26 ч"`; `(1,"X",10800,0,0)` → `"HLTB: сюжет 3 ч"`; все нули → `"HLTB: нет данных"`; `Id = null` → `"HLTB: не найдено"`.
  - `FetchAsync_SendsTokenAndPicks`: init → `{"token":"t1"}`, search → ответ из первого теста; результат `Id == 2127`, `Query == "Cyberpunk 2077"`; у запроса поиска заголовок `x-auth-token == "t1"`, `Referer`, `Origin` и `User-Agent` совпадает с init.
  - `FetchAsync_RetriesOnceOn403`: первый search → 403, второй → 200; init вызван 2 раза, результат найден. Если search всегда 403 → `ThrowsAnyAsync`.
  - `FetchAsync_NoMatchReturnsNullId`: `{"data":[]}` → `Id == null`, `Fetched == now`.

  В `LibraryTests.cs` добавить `Hltb_RoundTripsAndMissingIsNull`: сохранить `Library` с `Overrides["C:\\G"].Hltb = new(5,"G",1,2,3,"G",now)` во временный файл, `Library.Load` → равен исходному record; JSON переопределения без `Hltb` → `Hltb == null`.

- [ ] **Step 2: Убедиться, что тесты падают.** Run: `dotnet test --filter "FullyQualifiedName~HltbClientTests|FullyQualifiedName~LibraryTests"`. Expected: ошибка компиляции, `HltbClient`/`HltbInfo` не определены.

- [ ] **Step 3: Реализовать** `HltbInfo` + `GameOverride.Hltb` в `Game.cs` и `HltbClient.cs`:
  - Тело POST — дословно из спеки, `searchTerms` = `SearchTerms(name)`; сериализуется через анонимный объект или `JsonNode`.
  - Токен — в поле экземпляра; init выполняется, если токена нет, и ещё раз после 403. Неуспешный статус — `EnsureSuccessStatusCode`.
  - `Normalize`: `ToLowerInvariant`, убрать `™®©`, всё, что не буква/цифра, → пробел, схлопнуть пробелы, `Trim`. `SearchTerms`: убрать `™®©`, `Split(' ', RemoveEmptyEntries)`.
  - `Format`: части `сюжет`/`+ доп.`/`100%` для ненулевых значений через ` · `. Время: `мин = round(с/60)`; `мин < 60` → `"{мин} мин"`; иначе `ч = с/3600`; `ч < 10` → округлить до 0,5 и вывести с культурой `ru-RU` (`"2,5 ч"`, `"3 ч"`); иначе `"{round(ч)} ч"`.

- [ ] **Step 4: Прогнать тесты.** Run: `dotnet test`. Expected: все зелёные, в том числе старые.

- [ ] **Step 5: Commit.**

```bash
git add src/metGameHUB/Scan/Game.cs src/metGameHUB/Hltb tests/metGameHUB.Tests/Hltb tests/metGameHUB.Tests/Scan/LibraryTests.cs
git commit -m "HltbClient: search How Long to Beat, pick match, cache rules"
```

### Task 2: Строка на странице игры и документация

**Files:**
- Modify: `src/metGameHUB/Ui/GameRow.cs` — свойства HLTB рядом с `SearchName` (~строка 103)
- Modify: `src/metGameHUB/Ui/MainWindow.xaml.cs` — поле клиента (~строка 30), `OpenGamePage` (~211), обработчики
- Modify: `src/metGameHUB/Ui/MainWindow.xaml` — строка в шапке (StackPanel ~216–221, между `Details` и `ParamsToggle`); `LostFocus` у `TextBox` «Имя для поиска» (~239)
- Modify: `CLAUDE.md`, `docs/backlog.md`

**Interfaces:**
- Consumes: всё из блока Produces Task 1.
- Produces (`GameRow`):
  - `HltbInfo? Hltb { get; set; }` — `lib.For(Game).Hltb`, сеттер сохраняет библиотеку (`Save()`) и поднимает `PropertyChanged` для `Hltb`, `HltbText`, `HltbVisible`, `HltbTip`.
  - `bool HltbLoading { get; set; }` — поднимает те же уведомления.
  - `string HltbText` — `HltbLoading ? "HLTB: …" : Hltb is { } h ? HltbClient.Format(h) : ""`.
  - `bool HltbVisible` — `HltbLoading || Hltb != null`.
  - `string HltbTip` — найдено: `"{Name} на HowLongToBeat"`; `Id == null`: `"Поменяйте «Имя для поиска» в параметрах"`.

- [ ] **Step 1: `GameRow`** — добавить свойства выше. Отдельных тестов нет: вся логика — в покрытых `Format`/`IsFresh`.

- [ ] **Step 2: `MainWindow`:**
  - `readonly HltbClient hltb = new(Http);`
  - `async void LoadHltb(GameRow row)`: выход, если `row.HltbLoading` или `HltbClient.IsFresh(row.Hltb, lib.SearchNameOf(row.Game), DateTime.Now)`; иначе `HltbLoading = true`, `row.Hltb = await hltb.FetchAsync(name, DateTime.Now)`, `catch (Exception ex)` → `Log.Write($"HLTB {name}: {ex.Message}")`, `finally HltbLoading = false`.
  - Вызов `LoadHltb(row)` в конце `OpenGamePage`.
  - `SearchName_LostFocus` → `if (GamePage.DataContext is GameRow row) LoadHltb(row)` (взять тот же способ получения текущей строки, что у соседних обработчиков страницы).
  - `Hltb_Click` → если `row.Hltb?.Id is int id`: `Process.Start(new ProcessStartInfo(HltbClient.GameUrl(id)) { UseShellExecute = true })`.

- [ ] **Step 3: XAML** — в StackPanel шапки после `TextBlock` с `Details`: `Button` `Style="{StaticResource LinkButton}"`, `Content="{Binding HltbText, Mode=OneWay}"`, `ToolTip="{Binding HltbTip, Mode=OneWay}"`, `Foreground="{DynamicResource TextSecondary}"`, `Visibility="{Binding HltbVisible, Converter={StaticResource BoolToVisibility}}"`, `HorizontalAlignment="Left"`, `Click="Hltb_Click"`. Если неявный стиль `TextBlock` перебивает цвет внутри `LinkButton` (см. CLAUDE.md), цвет задаётся через Style, как у бейджей. У `TextBox` «Имя для поиска» — `LostFocus="SearchName_LostFocus"`.

- [ ] **Step 4: Сборка и тесты.** Run: `dotnet build` и `dotnet test`. Expected: 0 ошибок, все тесты зелёные.

- [ ] **Step 5: Живая проверка** (через терминал пользователя, не из песочницы: `%AppData%` там подменён). Запустить `src/metGameHUB/bin/Debug/net8.0-windows/metGameHUB.exe` и проверить:
  - игра из Steam с известным именем → «HLTB: …», затем время; щелчок открывает страницу HLTB; в подсказке видно имя;
  - повторное открытие страницы → строка сразу из кэша (в `library.json` появилось `Hltb`);
  - «Имя для поиска» = «zzzz qqqq», уйти из поля → «HLTB: не найдено»; вернуть имя → время снова;
  - быстро переключиться на другую игру сразу после открытия → у первой игры время сохранилось в `library.json`;
  - в `log.txt` нет `HLTB` ошибок.

- [ ] **Step 6: Документация.**
  - CLAUDE.md: `Hltb/` `HltbClient` (How Long to Beat) — в «Структуре»; в «Данных» к `library.json` — `Hltb` (время HLTB в секундах, id, имя, запрос, время загрузки); в «Неочевидном» — одна строка: init → token → POST `/api/search/site` с `x-auth-token`, один константный UA (токен привязан к IP+UA), 403 → повтор один раз, кэш 30/7 дней по `Query`, ошибки не кэшируются, сайт меняет схему раз в несколько месяцев — чинить в `HltbClient`.
  - `docs/backlog.md`: удалить пункт How Long to Beat из «Новых фич» и из «Порядка крупных тем», добавить его в список сделанных во вступлении (без номера версии).

- [ ] **Step 7: Commit.**

```bash
git add src/metGameHUB/Ui CLAUDE.md docs/backlog.md
git commit -m "Game page: How Long to Beat line"
```
