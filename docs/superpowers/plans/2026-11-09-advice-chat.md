# Корректировка совета в чате — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** под таблицей совета на странице игры — чат, в котором пользователь текстом, скриншотом, файлом или голосом (Win+H) просит поправить совет, а модель возвращает исправленный совет и короткую реплику.

**Architecture:** `ChatAttachments` готовит вложения в блоки content; `ClaudeClient.AdjustAsync` шлёт многоходовый диалог (контекст первым блоком первого `user`-сообщения, история парами, вложения в последнем ходе); `Advisor.AdjustAsync` собирает контекст как подбор и сохраняет совет и пару ходов в `GameOverride.Chat`; UI — разметка в `GamePage`, логика в `Ui/ChatPanel.cs`.

**Tech Stack:** .NET 8, WPF, System.Text.Json, xUnit; сторонних пакетов нет.

**Spec:** `docs/superpowers/specs/2026-11-09-advice-chat-design.md`

## Global Constraints

- Сторонних NuGet-пакетов кроме xUnit нет; комментарии только там, где поведение неочевидно.
- Тесты в `tests/metGameHUB.Tests/Advice/` — namespace `MetGameHUB.Tests` (не `.Advice`).
- `MaxHistoryTurns = 20`; `ChatAttachments.MaxImageSide = 1568`; картинка ≤ 5 МБ после подготовки; PDF ≤ 20 МБ; текстовый файл ≤ 1 МБ на диске, в запрос до 32 К символов.
- Корректировка: модель и effort из `ClaudeChoice.Ask` (встроенный effort `"medium"`), `max_tokens` 8000, `web_search` `max_uses` 2, `fallbacks: "default"`.
- Все строки интерфейса и ошибок — по-русски, тексты из спеки дословно: «Модель думает…», «Задайте ключ API в настройках», «Текст и вложения уходят в Anthropic API», «Тип файла не поддерживается», «После применения стало лучше: », «После применения стало хуже: ».
- Все цвета в XAML — через `DynamicResource`.

## Review Focus

1. Модель ответила `reply`, но пустым `settings` («менять нечего») — таблица не должна опустеть: прошлые пункты совета сохраняются, добавляется только реплика (Task 3, тест `AdjustAsync_EmptySettings_KeepsPreviousSettings`).
2. Отправка только вложений без текста — в последнем сообщении нет пустого блока `text` (API его отвергнет) (Task 2, тест `BuildAdjustBody_EmptyText_OnlyAttachmentBlocks`); пусто и без вложений — кнопка «Отправить» недоступна (Task 4).
3. Чат без совета (совет ещё не подобран, или очищен) — `AdjustAsync` бросает `AdviceException("Сначала подберите настройки")`, а не NRE (Task 3, тест `AdjustAsync_NoAdvice_Throws`).
4. Ctrl+V, когда в буфере текст, а не картинка, — обычная вставка текста, вложение не создаётся (Task 4, ручная проверка).
5. Уход со страницы или выбор другой игры во время корректировки — тот же `ConfirmLeave` и отмена, поле и вложения не теряются при ошибке (Task 4, ручная проверка).

---

### Task 1: Модели чата и подготовка вложений

**Files:**
- Modify: `src/metGameHUB/Advice/Models.cs` (рядом с `Advice:16`)
- Modify: `src/metGameHUB/Scan/Game.cs:27` (после `Snapshot`)
- Create: `src/metGameHUB/Advice/ChatAttachment.cs`
- Test: `tests/metGameHUB.Tests/Advice/ChatAttachmentTests.cs`

**Interfaces:**
- Produces:
  - `public enum ChatRole { User, Assistant }`
  - `public sealed record ChatTurn(ChatRole Role, string Text, List<string>? Attachments, DateTime At);`
  - `GameOverride.Chat : List<ChatTurn>?` (null — истории нет)
  - `public sealed record ChatAttachment(string Name, object Block);` — `Block` сериализуется в блок content Messages API
  - `public static class ChatAttachments { public const int MaxImageSide = 1568; public static ChatAttachment Prepare(string path); public static ChatAttachment FromImage(BitmapSource image, string name); }` — ошибки `AdviceException` с причиной по-русски

Формы блоков (точно):

```json
{"type":"image","source":{"type":"base64","media_type":"image/png","data":"…"}}
{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"…"}}
{"type":"text","text":"Файл a.log:\n…"}
```

- [ ] **Step 1: Тесты** в `ChatAttachmentTests` (временная папка, файлы создаются в тесте; блок проверять через `JsonSerializer.Serialize(a.Block)` + `JsonDocument`):
  - `Prepare_LargePng_DownscaledTo1568` — PNG 3136×1000 (создать через `WriteableBitmap` + `PngBitmapEncoder`) → `type == "image"`, `media_type == "image/png"`, декодированная картинка из `data` имеет `PixelWidth == 1568`, `PixelHeight == 500`.
  - `Prepare_SmallJpg_KeptAsJpeg` — JPEG 100×100 → `media_type == "image/jpeg"`, байты `data` равны байтам файла.
  - `Prepare_Pdf_DocumentBlock` — файл `a.pdf` с байтами `%PDF-1.4` → `type == "document"`, `media_type == "application/pdf"`, `data` = base64 файла.
  - `Prepare_TextFile_MaskedAndTruncated` — `a.log` с `password=hunter2` и 40 000 символов → `type == "text"`, текст начинается с `"Файл a.log:\n"`, содержит `password=***`, не содержит `hunter2`, длина содержимого ≤ 32 768 + пометка обрезки.
  - `Prepare_UnsupportedOrTooLarge_Throws` — `a.exe` → `AdviceException` с сообщением «Тип файла не поддерживается»; `.log` больше 1 МБ → `AdviceException`.
- [ ] **Step 2:** `dotnet test --filter ChatAttachmentTests` — не компилируется.
- [ ] **Step 3: Реализация.** `ChatTurn`/`ChatRole` в `Models.cs`, `Chat` в `GameOverride`. `ChatAttachments.Prepare` по расширению (регистр не важен):
  - `.png .jpg .jpeg` — `BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)`; если длинная сторона ≤ 1568 и файл ≤ 5 МБ — байты как есть с исходным media_type; иначе `TransformedBitmap` + `ScaleTransform` до 1568 по длинной стороне → `PngBitmapEncoder`, если результат > 5 МБ — `JpegBitmapEncoder` (QualityLevel 90), всё ещё > 5 МБ — отказ.
  - `.gif .webp` — как есть, если ≤ 5 МБ (`image/gif`, `image/webp`), иначе отказ.
  - `.pdf` — ≤ 20 МБ, иначе отказ.
  - `.txt .log .ini .cfg .json .csv .xml` — ≤ 1 МБ; UTF-8 при валидных байтах (`new UTF8Encoding(false, true)`), иначе Latin1; `ConfigFinder.MaskSecrets`; обрезка до 32 768 символов с хвостом `"\n[обрезано]"`.
  - `FromImage` — та же ветка уменьшения/кодирования для `BitmapSource` (общий приватный метод).
  - Если WPF-кодеки падают в MTA-потоке тестов — тесты гонять через хелпер, запускающий тело в STA-потоке (`Thread` + `SetApartmentState(STA)`), код не менять.
- [ ] **Step 4:** `dotnet test --filter ChatAttachmentTests` — PASS; `dotnet test` — всё зелёное.
- [ ] **Step 5: Commit** `Chat models and attachment preparation`.

### Task 2: Запрос корректировки в ClaudeClient

**Files:**
- Modify: `src/metGameHUB/Advice/ClaudeClient.cs` (записи рядом с `AdviceRequest:11`; промпт после `AskPrompt`; методы рядом с `AskAsync`/`BuildAskBody`; `Body` — параметр сообщений)
- Test: `tests/metGameHUB.Tests/Advice/ClaudeClientTests.cs`

**Interfaces:**
- Consumes: `ChatTurn`, `ChatRole`, `ChatAttachment` (Task 1).
- Produces:
  - `public sealed record AdjustRequest(AdviceRequest Context, AdviceResult Advice, IReadOnlyList<ChatTurn> History, string Text, IReadOnlyList<ChatAttachment> Attachments);`
  - `public sealed record AdjustResult(string Reply, string Summary, List<SettingAdvice> Settings, string? Notes);`
  - `public const int MaxHistoryTurns = 20;` (в `ClaudeClient`)
  - `public Task<AdjustResult> AdjustAsync(AdjustRequest request, CancellationToken ct)` — модель/effort через `ModelFor(choice?.Ask)` / `EffortFor(choice?.Ask, AskEffort)`
  - `internal static string BuildAdjustBody(AdjustRequest r, string model, string effort = AskEffort)`

Сборка `messages` (алгоритм, который сигнатура не задаёт):

```
history = r.History.TakeLast(MaxHistoryTurns); if history.First().Role == Assistant → skip 1
msgs = history.Select(t => {role, content: [text(t.Text + (t.Attachments?.Count > 0 ? "\n[вложения: a, b]" : ""))]})
last = {role:"user", content: (r.Text пусто ? [] : [text(r.Text)]) + r.Attachments.Select(a => a.Block)}
msgs.Add(last)
msgs[0].content.Insert(0, text(contextJson))   // contextJson — объект BuildAskBody (game, hardware, goal, config, current) + advice = r.Advice
```

`Body` получает вместо `object user` готовый `object messages` (существующие три Build-метода передают `new[] { new { role = "user", content = JsonSerializer.Serialize(user, Json) } }` — тела этих запросов не меняются, существующие тесты это проверяют).

`AdjustPrompt` — текст `AskPrompt` с изменениями: вводная «Тебе дают игру, железо, цель, файл настроек (если найден), текущие значения (current), прошлый совет (advice) и переписку с пользователем.»; новые правила, дословно из спеки:
- «Исправь совет по последней просьбе: меняй только то, о чём просят или что из этого прямо следует, остальные пункты верни без изменений.»
- «Если прислан скриншот или файл — опирайся на него (меню игры, оверлей с FPS, лог).»
- «reply — 1–3 предложения по-русски: что поменял и почему, или почему не стал менять.»
- в схеме ответа `required` добавить `"reply"`, в `properties` — `"reply":{"type":"string"}`.

- [ ] **Step 1: Тесты** (хелпер: разобрать `messages` из тела; первый блок первого сообщения — JSON контекста):
  - `BuildAdjustBody_RolesAlternate_ContextFirst` — история 2 хода (user «тени выше», assistant «поднял тени») + текст «ещё выше»: роли `user, assistant, user`; первый блок первого сообщения — JSON с `advice.summary`, `goal.mode == "balanced"`, `config.path`; второй блок — «тени выше»; последний блок последнего сообщения — «ещё выше». При пустой истории — одно сообщение, блоки `[контекст, текст]`.
  - `BuildAdjustBody_HistoryTrimmedTo20_StartsWithUser` — 24 хода (12 пар) → 21 сообщение; первое `user` и его второй блок — текст хода №5 (индекс 4); контекст в нём первым блоком.
  - `BuildAdjustBody_AttachmentBlocksInLastTurn` — вложения `image` и `text` → в последнем сообщении блоки `[text, image, text]` по порядку; ход истории с `Attachments = ["menu.png"]` даёт текст с `"\n[вложения: menu.png]"`.
  - `BuildAdjustBody_EmptyText_OnlyAttachmentBlocks` — `Text = ""`, одно вложение, история непустая → последнее сообщение состоит только из блока вложения.
  - `BuildAdjustBody_ModelEffortSearch` — через `AdjustAsync` с `ClaudeChoice(null, Ask: new() { Model = "claude-sonnet-5-5", Effort = "high" })`: `model`, `effort == "high"`, `tools[0].max_uses == 2`, `max_tokens == 8000`, `system` содержит «reply»; без `ClaudeChoice` — `claude-opus-5-5` и `"medium"`.
  - `AdjustAsync_ParsesReplyAndSettings` — ответ `{"reply":"Поднял тени","summary":"s","settings":[…1 пункт…],"notes":null}` → `Reply == "Поднял тени"`, один `SettingAdvice`.
- [ ] **Step 2:** `dotnet test --filter ClaudeClientTests` — не компилируется.
- [ ] **Step 3: Реализация** по Interfaces и алгоритму выше; контекст — тот же анонимный объект, что в `BuildAskBody` (вынести в `static object AskContext(AdviceRequest r)` и добавить `advice`).
- [ ] **Step 4:** `dotnet test` — всё зелёное, старые тесты тел запросов без изменений.
- [ ] **Step 5: Commit** `ClaudeClient: multi-turn adjust request`.

### Task 3: Advisor.AdjustAsync и очистка чата

**Files:**
- Modify: `src/metGameHUB/Advice/Advisor.cs` (`LocateAsync:38`, `AskAsync:48-78`, новый метод после `AskAsync`, `Restore:187`)
- Test: `tests/metGameHUB.Tests/Advice/AdvisorTests.cs`

**Interfaces:**
- Consumes: `AdjustRequest`, `AdjustResult`, `ClaudeClient.AdjustAsync` (Task 2); `ChatTurn`, `GameOverride.Chat` (Task 1).
- Produces: `public async Task<Advice> AdjustAsync(Game game, GoalStep goal, string text, IReadOnlyList<ChatAttachment> attachments, CancellationToken ct)` (цель передаётся как в `AskAsync`; в спеке её нет в сигнатуре — это уточнение).

- [ ] **Step 1: Тесты** (Env как у существующих; `o.Advice = e.AdviceFor()` заранее):
  - `AdjustAsync_SavesAdviceAndBothTurns` — ответ с `reply` и одним пунктом → `o.Advice.Result.Settings` из ответа, `o.Chat` = `[User "тени выше" Attachments ["a.png"], Assistant "Поднял"]`, `Library.Load(e.LibPath)` видит то же.
  - `AdjustAsync_EmptySettings_KeepsPreviousSettings` — ответ с `"settings":[]` → пункты прошлого совета на месте, `Summary`/`Notes` из ответа, реплика в `Chat`.
  - `AdjustAsync_ApiError_KeepsAdviceAndChat` — Fake отдаёт 500 (добавить в `Fake` статус-параметр или отдельный хендлер) → `AdviceException`, `o.Advice` и `o.Chat` не изменились.
  - `AdjustAsync_NoKey_Throws` — `apiKey` = null → `AdviceException("Не задан ключ API")`, запрос не ушёл.
  - `AdjustAsync_NoAdvice_Throws` — `o.Advice = null` → `AdviceException("Сначала подберите настройки")`.
  - `AdjustAsync_SendsHistoryAndFreshCurrent` — `o.Chat` с одной парой, свежий снимок → в теле 3 сообщения, контекст содержит `current` и `advice`.
  - `AskAsync_ClearsChat`, `Restore_ClearsChat`, `LocateAsync_NewPath_ClearsChat` — `o.Chat` непустой до вызова, null после (у `LocateAsync` — только при смене пути; при том же пути чат остаётся).
- [ ] **Step 2:** `dotnet test --filter AdvisorTests` — красные/не компилируются.
- [ ] **Step 3: Реализация.** Вынести из `AskAsync` сборку `AdviceRequest` (строки 51–59) в приватный метод, вызываемый и из `AdjustAsync`. `AdjustAsync`: `Client()` первым (ключ), затем проверка `o.Advice`, запрос, затем — только после успеха — `Advice` (при пустом `Settings` взять прошлые) и два хода в `Chat`, `lib.Save`. Очистка `o.Chat = null`: в `AskAsync` при сохранении совета, в `LocateAsync` рядом с `o.Snapshot = null`, в `Restore` в блоке `if (lib.Overrides.TryGetValue(...))`.
- [ ] **Step 4:** `dotnet test` — всё зелёное.
- [ ] **Step 5: Commit** `Advisor: adjust advice in chat, clear chat on new advice`.

### Task 4: Чат на странице игры и документация

**Files:**
- Modify: `src/metGameHUB/Ui/GameRow.cs` (свойства рядом с `HasAdvice:195`; `LoadAdvice:261`)
- Modify: `src/metGameHUB/Ui/MainWindow.xaml` (лента — в `ScrollViewer` `GamePage` после `AdviceAt:335`; поле ввода — новая строка `Grid` `GamePage` под кнопками `:340-347`)
- Create: `src/metGameHUB/Ui/ChatPanel.cs`
- Modify: `src/metGameHUB/Ui/MainWindow.xaml.cs` (создание `ChatPanel`; отправка через общий `asking`/`SetAsking`/`SetStatus`/`RefreshRows`, `RunAdviceAsync:413` как образец)
- Modify: `CLAUDE.md`, `README.md`, `docs/backlog.md`

**Interfaces:**
- Consumes: `Advisor.AdjustAsync` (Task 3), `ChatAttachments.Prepare/FromImage` (Task 1), `GameOverride.Chat`.
- Produces (для XAML-биндингов): `GameRow.Chat : IReadOnlyList<ChatTurnVm>`, `GameRow.CanChat : bool` (= `HasAdvice && HasKey && !IsAsking`), `GameRow.WasApplied : bool` (= есть `<конфиг>.bak`); `public sealed record ChatTurnVm(bool Mine, string Text, string Attachments, string At);` (`Attachments` — «📎 a.png, b.log» или "").

- [ ] **Step 1: GameRow.** `Chat` строится в `LoadAdvice` из `o.Chat`; `WasApplied` — в `RefreshConfig` (рядом с `canRestore`); уведомления добавить в существующие списки `Notify`. `CanChat` уведомлять там же, где `CanAsk`.
- [ ] **Step 2: XAML.** Блок виден при `HasAdvice`. Лента — `ItemsControl` по `Chat`: свои сообщения `HorizontalAlignment=Right`, модели — `Left` (DataTrigger по `Mine`), фон `SurfaceHover`/`Surface` через `DynamicResource`, `MaxWidth` 520, текст с переносом, строка «📎 …» и время мелким `TextSecondary`; ссылка «Очистить». Поле: плашки вложений (`ItemsControl` с крестиком), `TextBox` `AcceptsReturn=True` `TextWrapping=Wrap` `MaxHeight` 120, `AllowDrop=True`; кнопки «📎», «🎤», «Отправить» (`AccentButton`), «👍 Стало лучше» / «👎 Стало хуже» (видимы при `WasApplied`); подпись «Текст и вложения уходят в Anthropic API»; при `NoKey` вместо поля — «Задайте ключ API в настройках»; при `IsAsking` — «Модель думает…».
- [ ] **Step 3: ChatPanel.** Класс над элементами поля (передаются в конструктор): список вложений (`ObservableCollection<PendingAttachment>` где `PendingAttachment(string Name, ChatAttachment? Ready, string? Error)`); «📎» — `OpenFileDialog` (`Multiselect`, фильтр из расширений Task 1); `Drop` — те же пути; `PreviewKeyDown`: Enter без Shift — отправка, Ctrl+V при `Clipboard.ContainsImage()` — `FromImage(Clipboard.GetImage(), $"скриншот-{DateTime.Now:HHmmss}.png")` и `e.Handled = true`, иначе обычная вставка; «🎤» — `Focus()` поля, затем Win+H через `keybd_event` (P/Invoke `user32`, `VK_LWIN = 0x5B`, `'H' = 0x48`, нажатие и отпускание обоих); 👍/👎 — вставить заготовку из Global Constraints и `CaretIndex` в конец. «Отправить» доступна, если `CanChat` и (текст непуст или есть готовое вложение). Событие `Send(string text, IReadOnlyList<ChatAttachment> ready)` наружу; `Clear()` после успеха.
- [ ] **Step 4: MainWindow.** Обработчик `Send` — по образцу `RunAdviceAsync`: если `asking != null` — выход; общий `CancellationTokenSource`, `SetAsking`, `SetStatus(row, "Модель думает…", "")`, `advisor.AdjustAsync(row.Game, row.Goal, text, ready, ct)`, затем `RefreshRows(row)`, `chatPanel.Clear()`, прокрутка ленты вниз; ошибки — как в `RunAdviceAsync` (`AdviceException` → статус, поле и вложения не трогать). «Очистить» — `MessageBox` с подтверждением, `o.Chat = null`, `lib.Save`, `RefreshRows`.
- [ ] **Step 5:** `dotnet build` без ошибок и предупреждений; `dotnet test` зелёный.
- [ ] **Step 6: Документация.** `CLAUDE.md`: в «Данные» — поле `Chat` и когда очищается; в «Неочевидное» — поток корректировки (`AdjustAsync`: контекст первым блоком первого `user`-сообщения, история парами, 20 ходов, вложения только в своём ходе, пустой `settings` сохраняет прошлые пункты, модель/effort как у подбора) и что уходит в API (текст, картинки ≤1568 px, PDF, текстовые файлы с маской секретов); в «Структура» — `ChatAttachment`, `ChatPanel`. `README.md` — абзац про уточнение совета в чате. `docs/backlog.md` — снять «Корректировку» и «Обратную связь», перенумеровать порядок.
- [ ] **Step 7: Ручная проверка** на запущенном приложении (через терминал пользователя, не из песочницы): отправка текста; Ctrl+V скриншота (Win+Shift+S) и Ctrl+V текста; перетаскивание `.log` и `.exe` (плашка с отказом); 🎤 открывает голосовой ввод; 👍 вставляет заготовку; уход со страницы во время запроса спрашивает подтверждение; после перезапуска история на месте; «Подобрать» очищает чат.
- [ ] **Step 8: Commit** `Advice chat on the game page`.
