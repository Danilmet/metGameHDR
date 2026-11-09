# Конфиги Unity в реестре (PlayerPrefs)

## Цель

Советчик по графике работает с играми на Unity, которые хранят настройки в реестре (`HKCU\Software\<Компания>\<Продукт>`, PlayerPrefs), так же, как с INI/JSON-файлами: найти → показать «Сейчас» → подобрать → применить → вернуть исходное. Третий формат конфига — `registry`.

## Решения

- **Ключ хранится в том же `ConfigPath`** строкой `HKCU\Software\…`; `library.json` не меняется, старые записи работают как раньше. Различение «файл или ключ» — по префиксу `HKCU\`, в одном месте (`ConfigStore`).
- **Поиск решает модель.** Приложение только подсказывает: читает `<Игра>_Data\app.info` в папке установки (две строки: компания и продукт) и отдаёт модели собранный ключ полем `unityPlayerPrefs`, существует ключ в реестре или нет (PlayerPrefs появляются после первого запуска, как и файлы). Часть Unity-игр хранит графику в своём JSON, поэтому без запроса ключ не берётся.
- **Копия и откат — через `reg.exe`** (`reg export` / `reg import`): кода почти нет, а `.reg` человек может импортировать сам, если приложения уже нет.
- **Новые значения не создаются.** Имя значения Unity заканчивается хэшем (`Screenmanager Resolution Width_h182942802`), выдумать его нельзя; пишутся только существующие значения с сохранением типа.
- **Игра должна быть закрыта**: Unity перезаписывает PlayerPrefs при выходе. Проверка та же, что у файлов.

## Допуск ключа

`RegistryConfig.IsAllowedKey(string key)`: после нормализации (`HKEY_CURRENT_USER\` → `HKCU\`, обрезка завершающих `\`) ключ начинается с `HKCU\Software\`, под `Software` не меньше двух сегментов, первый сегмент не `Microsoft`, `Classes`, `Policies`, `Wow6432Node` (без учёта регистра). Проверяется при сохранении пути из ответа модели и при каждом чтении сохранённого пути (`Advisor.LocatedPath`), как у файлов `IsAllowedPath`. HKLM и вложенные подключи — вне рамок.

## Выгрузка модели

`RegistryConfig.Dump(string key)` → текст строками `имя=значение`, отсортированный по имени (ordinal, без учёта регистра), имена как есть, с хвостом `_h<хэш>`. Типы:

| Тип в реестре | В выгрузке | Классификация для записи |
|---|---|---|
| REG_DWORD, значение ≤ 1 000 000 или как float не конечное | целое десятичное | `int` |
| REG_DWORD, значение > 1 000 000 и как float конечное | float инвариантно (`0.75`) | `float` |
| REG_BINARY, валидный UTF-8 (без завершающего нуля) | строка | `binaryString` |
| REG_SZ | строка | `sz` |
| прочее (QWORD, MULTI_SZ, нечитаемый бинарник) | не попадает | не пишется |

Unity хранит float в DWORD битами IEEE 754; любое нормальное float имеет биты ≥ 8 388 608, а целые настройки (разрешение, индекс качества, режим окна) много меньше миллиона, поэтому порог в миллион разделяет их надёжно.

Дальше выгрузка идёт тем же путём, что текст файла: `ConfigFinder.MaskSecrets`, лимит 32 К символов с флагом `truncated`, `config.format = "registry"`, `config.path` — ключ. `UnrealScalability.Merge` на выгрузке ничего не находит и ничего не меняет.

## Промпты

- Поиск (`LocatePrompt`): `format` получает значение `registry` — путь это ключ `HKCU\Software\<Компания>\<Продукт>`, где Unity хранит PlayerPrefs; если игра держит графику в PlayerPrefs и прислан `unityPlayerPrefs`, вернуть его как есть. Правило «none для реестра» убирается. В схеме `enum: ["ini","json","registry","none"]`. В тело запроса добавляется `unityPlayerPrefs` (строка или null).
- Подбор, чтение и уточнение: одна фраза в правило про `section`/`key`: для `format: "registry"` `section` всегда null, `key` — точное имя значения из выгрузки, включая хвост `_h…`; `value` — число или строка, как в выгрузке.

## Запись

`RegistryConfig.Apply(string key, IReadOnlyList<ConfigEdit> edits)` → `bool`:

1. Для каждой правки имя ищется среди существующих значений: точно (без учёта регистра), иначе по единственному значению, чьё имя до суффикса `_h<цифры>` совпадает с ключом правки. Не найдено или найдено несколько → `false`.
2. Тип берётся у существующего значения по классификации из таблицы: `int` → `int.Parse` инвариантно → REG_DWORD; `float` → `float.Parse` инвариантно → REG_DWORD с битами float; `binaryString` → UTF-8 + `\0` → REG_BINARY; `sz` → REG_SZ. Значение не парсится или тип не пишется → `false`.
3. Сначала проверяются все правки, затем пишутся все. `false` в `Advisor.Apply` даёт `ApplyOutcome.BadEdit`, как у INI.

`RegistryConfig.CurrentValue(string key, string name)` → строка по той же классификации или null; используется после отката, чтобы перечитать `Current`.

## Копия и откат

Папка `%AppData%\metGameHUB\registry\` (`AppPaths`). Имя файла: безопасное `<Компания>_<Продукт>` (последние два сегмента ключа, недопустимые символы → `_`) плюс `-` и 8 hex символов хэша нормализованного ключа, как у обложек. Суффиксы `.orig.reg` и `.bak.reg`.

- `Backup(key)`: `reg export "HKEY_CURRENT_USER\…" "<файл>" /y`. Перед первым применением пишется `.orig.reg` (если его ещё нет), перед каждым применением и откатом — `.bak.reg`. Семантика та же, что у `<файл>.orig`/`.bak`.
- `Restore(key)`: `reg import "<файл>.orig.reg"`. Значения, добавленные игрой после копии, остаются: для настроек это безвредно.
- `HasBackup(key)` — существует `.orig.reg`; `WasApplied(key)` — существует `.bak.reg`.
- `reg.exe` берётся из `%SystemRoot%\System32`; ненулевой код возврата → `IOException` с текстом stderr, уходит в UI тем же путём, что ошибки записи файла.

## Свежесть снимка

Поле `ConfigSnapshot.FileStamp` остаётся. Для ключа в него идёт время последней записи ключа: `RegQueryInfoKey` (advapi32, P/Invoke) через `SafeRegistryHandle` открытого ключа. Как и Unreal, Unity переписывает ключ при каждом выходе, поэтому «Конфиг изменился — Обновить» будет появляться после каждой сессии. Это известная тема из бэклога, здесь не решается.

## Компоненты

### `Advice/ConfigStore.cs` — статический диспетчер

Все операции над «конфигом по пути» для `Advisor` и `GameRow`; внутри ветка по `IsRegistry(path)`.

- `bool IsRegistry(string path)` — префикс `HKCU\` или `HKEY_CURRENT_USER\` без учёта регистра.
- `bool Exists(string path)`, `DateTime Stamp(string path)`.
- `string? ReadText(string path)` — файл: `File.ReadAllText` с ловлей IO-ошибок (нынешний `TryFileText`); ключ: `Dump`.
- `bool CanWrite(string path, string text, IReadOnlyList<ConfigEdit> edits)` — файл: нынешний `Advisor.CanWrite`; ключ: всегда true.
- `bool Apply(string path, IReadOnlyList<ConfigEdit> edits)` — файл: `IniEditor.ApplyBytes` + `.orig`/`.bak`/`.tmp` + `File.Move` (нынешний код `Advisor.Apply`); ключ: `Backup` в `.orig.reg` (если нет) и `.bak.reg`, затем `RegistryConfig.Apply`. `false` = `BadEdit`.
- `bool HasBackup(string path)`, `bool WasApplied(string path)`, `void Restore(string path)` — файл: нынешний код `Advisor.Restore` (`.orig` → `.tmp` → `File.Move`, текущий в `.bak`); ключ: `.bak.reg` + `reg import`.
- `string? CurrentValue(string path, string text, string? section, string key)` — файл: `IniEditor.CurrentValue(text, …)`; ключ: `RegistryConfig.CurrentValue`.

`Advisor` теряет прямые `File.*`, `IniEditor.ApplyBytes` и работу с `.orig`/`.bak`; `LocatedPath` для ключа проверяет `IsAllowedKey` вместо `ResolvePath`/`IsAllowedPath`. `ApplyOutcome.FileMissing` остаётся, в UI подпись «Файл не найден» → «Конфиг не найден».

### `Advice/RegistryConfig.cs`

`IsAllowedKey`, `Normalize`, `Exists`, `Stamp`, `Dump`, `Apply`, `CurrentValue`, `Backup`, `Restore`, `BackupPath(key, suffix)`. Чтение и запись через `Microsoft.Win32.Registry.CurrentUser`.

### `Advice/ConfigFinder.cs`

`string? UnityPlayerPrefsKey(string installDir)` — первая папка `*_Data` с файлом `app.info`; две непустые строки → `HKCU\Software\<строка1>\<строка2>`; иначе null. Ошибки ввода-вывода → null.

### `Advice/ClaudeClient.cs`

`LocateRequest` получает `string? UnityKey`; тело поиска — поле `unityPlayerPrefs`; изменения промптов из раздела «Промпты». `ConfigLocation.Format` допускает `registry`.

### `Advisor`

`LocateAsync`: для `Format == "registry"` путь нормализуется и проверяется `IsAllowedKey`; иначе, как сейчас, `TryResolve` + `IsAllowedPath`. `ReadConfig` для ключа берёт `Dump` и формат `registry`. `Prepare`, `ReadAsync`, `Apply`, `Restore`, `IsSnapshotFresh` — через `ConfigStore`.

### `Ui/GameRow.cs`, `Ui/MainWindow.xaml.cs`

`File.Exists(path)` → `ConfigStore.Exists`; `.orig`/`.bak` → `HasBackup`/`WasApplied`; `File.ReadAllText` в проверке `CanWrite` и в `ShowProvisionalFromFile` → `ConfigStore.ReadText`. Страница показывает ключ там же, где путь файла.

## Тесты

- `RegistryConfigTests` — реальный ключ `HKCU\Software\metGameHUB.Tests\<guid>`, создаётся в тесте и удаляется в `Dispose`: выгрузка int / float / строка REG_BINARY / REG_SZ / суффикс `_h`, пропуск QWORD и нечитаемого бинарника, сортировка; `Apply` сохраняет тип (int, float по битам, строка с нулём), ищет имя по префиксу, отказывает на неизвестное имя, на два кандидата по префиксу и на непарсящееся значение, ничего не пишет при отказе; `CurrentValue`; `IsAllowedKey` (допуск, `HKEY_CURRENT_USER`, `Microsoft`, один сегмент, HKLM); `Backup` → правка → `Restore` возвращает значения (через `reg.exe`).
- `ConfigFinderTests` — `UnityPlayerPrefsKey` по временной папке с `X_Data\app.info`; без файла — null.
- `ClaudeClientTests` — тело поиска содержит `unityPlayerPrefs`; разбор ответа с `format: "registry"`.
- `AdvisorTests` — `LocateAsync` с ответом `registry` и допустимым ключом сохраняет его; с `HKCU\Software\Microsoft\…` — path null и заметка о недопустимом пути; существующие тесты файлового пути проходят без изменений.

## Вне рамок

HKLM, вложенные подключи, создание новых значений, игры не на Unity с настройками в реестре, Steam Cloud для PlayerPrefs, сравнение снимка по содержимому вместо времени записи.
