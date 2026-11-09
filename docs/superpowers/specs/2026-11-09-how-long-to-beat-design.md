# How Long to Beat на странице игры

## Цель

На странице игры видно время прохождения с howlongtobeat.com: сюжет / сюжет + доп. / 100%. Это справка: на карточках сетки, в сортировке и подсчёте наигранного времени HLTB не участвует.

## Решения

- **Источник** — сам сайт, без Claude и без ключей. Официального API у сайта нет, схему он меняет примерно раз в 3–5 месяцев (последние поломки — март и август 2026). Поломку чинит новая версия хаба через самообновление. Адрес API по JS-бандлам сайта автоматически не ищем.
- **Когда загружать.** При открытии страницы игры, если кэша нет или он устарел, и после изменения «Имени для поиска». Фоновой загрузки для всей библиотеки нет.
- **Имя для поиска** — `Library.SearchNameOf(game)`, то же поле, что у обложек и конфигов.
- **Сверки по Steam appid нет**: промах исправляется вручную через «Имя для поиска», а имя игры на HLTB видно в подсказке.

## Запросы (проверено вживую 2026-11-09)

1. `GET https://howlongtobeat.com/api/search/site/init?t=<unix ms>` → `{"token":"..."}`.
2. `POST https://howlongtobeat.com/api/search/site` с заголовками `Content-Type: application/json`, `x-auth-token: <token>`, `Referer: https://howlongtobeat.com/`, `Origin: https://howlongtobeat.com`, `User-Agent: <браузерный>`. Тело:

```json
{"searchType":"games","searchTerms":["Cyberpunk","2077"],"searchPage":1,"size":20,
 "searchOptions":{"games":{"userId":0,"platform":"","sortCategory":"popular","rangeCategory":"main",
   "rangeTime":{"min":0,"max":0},"gameplay":{"perspective":"","flow":"","genre":"","difficulty":""},
   "rangeYear":{"max":"","min":""},"modifier":""},"users":{"sortCategory":"postcount"},
   "lists":{"sortCategory":"follows"},"filter":"","sort":0,"randomizer":0},"useCache":true}
```

`searchTerms` — имя, разбитое по пробелам. Ответ: `{"data":[{"game_id","game_name","game_type","comp_main","comp_plus","comp_100",...}]}`. Время в секундах, `0` = нет данных.

- Токен привязан к IP и `User-Agent`, поэтому на всё время работы приложения берётся один фиксированный `User-Agent` (константа). Токен хранится в памяти до перезапуска; при 403 запрашивается заново, и поиск повторяется один раз.
- Страница игры: `https://howlongtobeat.com/game/<game_id>`.

## Компоненты

### `Hltb/HltbClient.cs` (namespace `MetGameHUB.Hltb`)

- `HltbClient(HttpClient http)`, используется общий `MainWindow.Http` (таймаут 15 с).
- `Task<HltbInfo> FetchAsync(string name, CancellationToken ct = default)` — запросы init → search, затем выбор совпадения. Если ничего не подошло, возвращается `HltbInfo` с `Id = null`. При сетевой ошибке, неуспешном статусе (кроме однократного повтора на 403) и неожиданном JSON бросает исключение.
- Чистые статические функции (под тесты):
  - `List<HltbHit> Parse(string json)` — `data[]` → `HltbHit(int Id, string Name, string Type, int Main, int Plus, int Full)`. Отсутствующее или `null`-поле времени = 0.
  - `HltbHit? Pick(IReadOnlyList<HltbHit> hits, string name)` — отбрасывает `Type != "game"`; среди оставшихся сначала ищет точное совпадение по `Normalize`, иначе берёт первый (сайт сортирует по популярности); если список пуст — `null`.
  - `string Normalize(string s)` — нижний регистр, без `™®©`, пунктуация заменяется пробелом, пробелы схлопываются, края обрезаются.
  - `bool IsFresh(HltbInfo? info, string query, DateTime now)` — `false`, если `info == null` или `info.Query != query`; иначе свежий не старше 30 дней при `Id != null` и не старше 7 дней при `Id == null`.
  - `string Format(HltbInfo info)` — текст строки (см. «Страница игры»).

### Модель (`Scan/Game.cs`)

```csharp
public sealed record HltbInfo(int? Id, string? Name, int Main, int Plus, int Full, string Query, DateTime Fetched);
```

`GameOverride.Hltb` (`HltbInfo?`), `null` = ещё не запрашивали. Лежит в `library.json` вместе с остальными переопределениями игры. Ошибки в кэш не попадают.

### `Ui/GameRow.cs`

- `HltbInfo? Hltb` — читается и пишется через `lib.For(Game).Hltb`, поднимает `PropertyChanged` для `Hltb`, `HltbText`, `HltbVisible`.
- `bool HltbLoading` — пока идёт запрос.
- `string HltbText`: при загрузке — «HLTB: …», иначе `HltbClient.Format(Hltb)`.
- `bool HltbVisible` — есть загрузка или кэш.

### `Ui/MainWindow`

- Поле `readonly HltbClient hltb = new(Http);`.
- `LoadHltbAsync(GameRow row)` вызывается при открытии страницы и при `LostFocus` поля «Имя для поиска». Если `IsFresh` — ничего не делает. Иначе ставит `HltbLoading`, вызывает `FetchAsync`, записывает результат в `row.Hltb` и сохраняет библиотеку так же, как остальные правки переопределений. При ошибке пишет `Log.Write` и ничего не сохраняет. Ответ записывается в свою игру, даже если пользователь уже переключился на другую, поэтому отмена не нужна. Пока для строки идёт запрос, повторный не запускается.

## Страница игры

Отдельная строка в шапке между деталями и «Параметры ▸»: `Button` со стилем `LinkButton` и вторичным цветом, `Visibility` по `HltbVisible`.

- Текст: `HLTB: сюжет 26 ч · + доп. 63 ч · 100% 109 ч`. Нулевые части не выводятся; если все нули — `HLTB: нет данных`; `Id == null` — `HLTB: не найдено`.
- Часы: меньше 1 ч — минуты («45 мин»); от 1 до 10 ч — с точностью до получаса («2,5 ч», «3 ч»); от 10 ч — целые («26 ч»). Округление к ближайшему.
- Подсказка: имя игры на HLTB («Cyberpunk 2077 на HowLongToBeat»); для «не найдено» — «Поменяйте «Имя для поиска» в параметрах».
- Щелчок открывает `https://howlongtobeat.com/game/<id>` в браузере (`Process.Start` с `UseShellExecute = true`). Для «не найдено» щелчок ничего не делает.
- При ошибке строка скрыта (кэша нет, загрузки нет).

## Тесты (`tests/metGameHUB.Tests/HltbTests.cs`)

- `Parse_ReadsSecondsAndMissingAsZero` — поля в секундах; `null`/отсутствующее время = 0.
- `Pick_PrefersExactNormalizedName` — «Cyberpunk 2077™» совпадает с «cyberpunk 2077», даже если первый в выдаче — другой.
- `Pick_FallsBackToFirstGame` — без точного совпадения берётся первый с `game_type == "game"`.
- `Pick_SkipsDlc` — DLC не выбирается, даже если стоит первым и точно совпадает.
- `Pick_EmptyReturnsNull`.
- `IsFresh_QueryChangedIsStale`, `IsFresh_FoundExpiresAfter30Days`, `IsFresh_NotFoundExpiresAfter7Days`.
- `Format_HoursRounding` — 2700 с → «45 мин», 9000 → «2,5 ч», 94172 → «26 ч»; нулевые части пропускаются; все нули → «нет данных»; `Id == null` → «не найдено».

HTTP-часть проверяется вживую: открыть страницу пары игр и посмотреть строку и ссылку.

## Документация

- CLAUDE.md: `Hltb/` в структуре; `Hltb` в описании `library.json`; в «Неочевидном» — рецепт запроса (init → token, `x-auth-token`, фиксированный UA), сроки кэша и что ошибки не кэшируются.
- `docs/backlog.md`: убрать пункт How Long to Beat и его строку из порядка крупных тем.
