# Корректировка совета в чате

Пункты бэклога «Корректировка подбора вместе с моделью» и «Обратная связь без телеметрии (лучше / хуже)».

## Зачем

После «Подобрать» совет можно только применить или подобрать заново с нуля. Нужно: пользователь уточняет совет словами («тени повыше», «просадки в городе», «верни сглаживание»), скриншотом, файлом или голосом, а модель правит прошлый совет с учётом снимка конфига и цели, не подбирая с нуля. Это же — заготовка для чата в оверлее.

Успех: на странице игры под таблицей совета есть лента чата и поле ввода; после ответа модели таблица «Сейчас | Рекомендуется» показывает исправленный совет, «Применить» пишет его так же, как обычный; история переживает перезапуск.

## Решения

- Многоходовый диалог Messages API: контекст первым ходом, дальше реплики по ролям, вложения — блоками content последнего хода пользователя.
- Голос — системный голосовой ввод Windows (Win+H) в поле ввода; своего распознавания речи нет.
- Вложение уходит модели только в своём сообщении; в истории остаётся пометка с именем файла. Файлы никуда не копируются.
- Модель и effort — из настроек подбора (`ClaudeAsk`, встроенный effort `medium`), веб-поиск до 2 обращений.
- «Лучше / хуже» — кнопки-заготовки текста, отдельной логики нет.
- Не делаем: отдельную задачу «Корректировка» в настройках модели, своё распознавание речи, повторную отправку старых вложений, стриминг ответа, кэширование промпта (форма запроса его допускает, включается отдельно).

## Данные

`Advice/Models.cs`:

```csharp
public enum ChatRole { User, Assistant }
public sealed record ChatTurn(ChatRole Role, string Text, List<string>? Attachments, DateTime At);
```

`GameOverride.Chat` (`List<ChatTurn>?`, null/пусто — истории нет). `Attachments` — только имена файлов для пометки; у хода модели null. Реплика модели хранится одним текстом (`reply`), таблица совета — по-прежнему в `GameOverride.Advice`.

Чат очищается (`Chat = null`):
- при новом «Подобрать» (`Advisor.AskAsync`);
- при смене пути конфига (там же, где сейчас сбрасывается снимок);
- при «Вернуть исходный конфиг» (`Advisor.Restore`);
- по ссылке «Очистить» (после подтверждения).

## Вложения

`Advice/ChatAttachment.cs` — подготовка одного файла к отправке:

```csharp
public sealed record ChatAttachment(string Name, object Block);   // Block — готовый блок content для JSON
public static class ChatAttachments
{
    public const int MaxImageSide = 1568;
    public static ChatAttachment Prepare(string path);              // AdviceException с причиной, если нельзя
    public static ChatAttachment FromImage(BitmapSource image, string name);   // для Ctrl+V
}
```

| Тип | Расширения | Блок | Ограничение |
|---|---|---|---|
| Картинка | `.png .jpg .jpeg .webp .gif` | `image`, base64 | длинная сторона > 1568 px уменьшается, перекодируется в PNG (JPEG — если PNG > 5 МБ); после этого > 5 МБ — отказ |
| PDF | `.pdf` | `document`, base64 `application/pdf` | файл > 20 МБ — отказ |
| Текст | `.txt .log .ini .cfg .json .csv .xml` | `text`: `Файл <имя>:\n<содержимое>` | файл > 1 МБ — отказ; содержимое до 32 К символов (обрезка с пометкой), `ConfigFinder.MaskSecrets` |
| Другое | — | — | отказ «Тип файла не поддерживается» |

`.gif` и `.webp` отправляются как есть (без уменьшения), если ≤ 5 МБ. Текст читается как UTF-8 (с BOM или без), иначе Latin1 — как в редакторе INI.

## Запрос

`Advisor.AdjustAsync(Game game, string text, IReadOnlyList<ChatAttachment> attachments, CancellationToken ct) : Task<Advice>` → `ClaudeClient.AdjustAsync(AdjustRequest r, CancellationToken ct) : Task<AdjustResult>`.

```csharp
public sealed record AdjustRequest(AdviceRequest Context, AdviceResult Advice, IReadOnlyList<ChatTurn> History, string Text, IReadOnlyList<ChatAttachment> Attachments);
public sealed record AdjustResult(string Reply, string Summary, List<SettingAdvice> Settings, string? Notes);
```

`AdviceRequest Context` собирается так же, как в `Advisor.AskAsync` (тот же файл конфига, маска, `current` только при свежем снимке, цель игры).

Тело (`ClaudeClient.BuildAdjustBody(AdjustRequest r, string model, string effort = "medium")`): `max_tokens` 8000, `web_search` до 2, `output_config.effort`, `fallbacks: "default"`, `system` = `AdjustPrompt`, `messages`:

`Chat` всегда состоит из пар «пользователь → модель» (`AdjustAsync` сохраняет оба хода разом), поэтому история чередуется и начинается с хода пользователя.

1. История — последние 20 ходов `Chat` (`MaxHistoryTurns = 20`, то есть 10 пар), каждый — `{role, content: [text]}`; к тексту хода пользователя с вложениями дописывается `\n[вложения: a.png, b.log]`.
2. Новое `user`-сообщение — блок `text` с текстом пользователя, затем блоки вложений по порядку.
3. Контекст — JSON `game`, `hardware`, `goal {mode, fps}`, `config`, `current` (как в `BuildAskBody`) плюс `advice` (последний `AdviceResult` целиком) — вставляется **первым блоком `text` первого `user`-сообщения**: первого хода истории, а если история пуста — нового сообщения.

Итог: роли строго чередуются, первое и последнее сообщения — `user`, префилла нет.

`AdjustPrompt` — правила `AskPrompt` (цель, подписи меню, `section/key/value` только из присланного файла, русский язык) плюс:
- «Тебе дают прошлый совет (advice) и переписку с пользователем. Исправь совет по последней просьбе: меняй только то, о чём просят или что из этого прямо следует, остальные пункты верни без изменений.»
- «Если прислан скриншот или файл — опирайся на него (меню игры, оверлей с FPS, лог).»
- «reply — 1–3 предложения по-русски: что поменял и почему, или почему не стал менять.»
- Схема — схема подбора плюс обязательное поле `reply` (`string`).

Разбор — тот же `ClaudeClient.Parse<AdjustResult>`. В `Advisor.AdjustAsync` после успешного ответа:
- `Advice = new Advice(new AdviceResult(Summary, Settings, Notes), DateTime.UtcNow)`;
- в `Chat` добавляются ход пользователя (текст, имена вложений) и ход модели (`Reply`);
- `library.json` сохраняется одной записью.

При ошибке ничего не сохраняется. Без ключа — `AdviceException("Не задан ключ API")` до сборки запроса. Снимок конфига не трогается.

## Интерфейс (страница игры)

Разметка — в `GamePage` (`Ui/MainWindow.xaml`) под таблицей и кнопками «Применить»/«Отменить»; логика — в новом `Ui/ChatPanel.cs` (вложения, Ctrl+V, перетаскивание, Win+H, отправка), чтобы не раздувать `MainWindow.xaml.cs`. Блок виден только при наличии совета.

- **Лента**: свои сообщения справа, модели — слева; текст, пометки «📎 имя» и время. Таблицы в ленте нет — таблица над лентой всегда показывает последний совет. Ссылка «Очистить» — с подтверждением.
- **Поле ввода**: многострочный `TextBox`; Enter — отправить, Shift+Enter — перенос. Над полем — плашки вложений с крестиком; плашка с отказом показывает причину и не отправляется.
- **📎 Прикрепить** — `OpenFileDialog` (фильтр по таблице типов, мультивыбор).
- **Ctrl+V** с картинкой в буфере — `ChatAttachments.FromImage(Clipboard.GetImage(), "скриншот-ЧЧММСС.png")`; иначе обычная вставка текста.
- **Перетаскивание** файлов на поле — как «Прикрепить».
- **🎤 Голос** — фокус в поле, затем `SendInput` Win+H. Если голосовой ввод недоступен, Windows просто ничего не показывает; приложение ошибок не обрабатывает.
- **👍 Стало лучше / 👎 Стало хуже** — видны, если совет хотя бы раз применён (есть `.bak`); вставляют «После применения стало лучше: » / «После применения стало хуже: » и ставят курсор в конец.
- **Во время запроса**: поле и кнопки заблокированы, «Модель думает…»; уход со страницы отменяет запрос после подтверждения (как у подбора). После ответа таблица перестраивается через `AdviceTable.Build`, лента прокручивается вниз.
- **Без ключа API**: вместо поля — «Задайте ключ API в настройках».
- Подпись под полем: «Текст и вложения уходят в Anthropic API».

## Ошибки

- Вложение не подготовилось — плашка с причиной, в запрос не идёт.
- `AdviceException` (API, разбор, отказ модели) — текст в строке статуса страницы; поле, вложения и история остаются.
- Ключ из ответа, которого нет в файле, — существующие проверки `Advisor.Edits`/`Apply` (не пишется).
- Снимок устарел — `current` не отправляется, «Конфиг изменился — Обновить» остаётся.

## Тесты

`ClaudeClientTests`:
- `BuildAdjustBody_RolesAlternate_ContextFirst` — первое сообщение `user`, его первый блок — контекст (`advice`, `goal`, `config`), роли чередуются, последнее — `user` с текстом пользователя; при пустой истории контекст — первый блок единственного сообщения.
- `BuildAdjustBody_HistoryTrimmedTo20_StartsWithUser` — из 24 ходов (12 пар) уходят последние 20, первый — ход пользователя, и контекст стоит в нём.
- `BuildAdjustBody_AttachmentBlocksInLastTurn` — блоки `image`/`document`/`text` в последнем сообщении в порядке вложений; в истории — пометка `[вложения: …]`.
- `BuildAdjustBody_ModelEffortSearch` — модель и effort как у подбора, `web_search` `max_uses` 2.
- `AdjustAsync_ParsesReplyAndSettings`.

`AdvisorTests`:
- `AdjustAsync_SavesAdviceAndBothTurns`.
- `AdjustAsync_ApiError_KeepsAdviceAndChat`.
- `AdjustAsync_NoKey_Throws`.
- `AskAsync_ClearsChat`, `Restore_ClearsChat`, `LocateAsync_NewPath_ClearsChat`.

`ChatAttachmentTests`:
- `Prepare_LargePng_DownscaledTo1568`.
- `Prepare_Pdf_DocumentBlock`.
- `Prepare_TextFile_MaskedAndTruncated`.
- `Prepare_UnsupportedOrTooLarge_Throws`.

Win+H, Ctrl+V, перетаскивание и вид ленты проверяются руками на запущенном приложении.

## Документация

- `CLAUDE.md`: поток корректировки (`AdjustAsync`, контекст первым ходом, 20 ходов, вложения только в своём ходе), поле `Chat` в `library.json` и когда оно очищается, что уходит в API (текст, вложения: картинки, PDF, текст с маской секретов).
- `README`: раздел советчика — уточнение совета в чате.
- `docs/backlog.md`: снять «Корректировку» и «Обратную связь»; порядок крупных тем перенумеровать.
