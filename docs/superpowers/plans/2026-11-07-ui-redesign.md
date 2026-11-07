# metGameHDR UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. План намеренно без кода: сигнатуры и тесты описаны словами, реализацию пишет исполнитель задачи.

**Goal:** Заменить таблицу игр карточками с обложками в OLED/Apple-стиле, с системным стеклом и темой, следующей за Windows, не меняя логику слежения, сканирования и записи в Steam.

**Architecture:** Вся новая работа в `src/metGameHDR/Ui/` и `App.xaml`: класс `Covers` находит обложку на диске (кэш Steam, grid Steam, собственный кэш) и докачивает недостающие из SteamGridDB в фоне; `Theme` читает системную тему и подменяет словарь ресурсов; `Backdrop` включает системный Acrylic через DWM. `MainWindow.xaml` перестраивается на сегмент-переключатель, `ListBox` карточек и панель деталей; имена контролов и привязки сохраняются, чтобы `MainWindow.xaml.cs` почти не менялся. Единственное касание вне `Ui/` — необязательное поле `SteamAppId` у `Game`.

**Tech Stack:** .NET 8, WPF, WinForms (`SystemEvents`), P/Invoke `dwmapi`, xUnit. Сторонних пакетов нет.

**Spec:** `docs/superpowers/specs/2026-11-07-ui-redesign-design.md`

## Global Constraints

- Никаких NuGet-пакетов кроме xUnit в тестах.
- Логика в `Scan/`, `Watch/`, `Steam/` не меняется, кроме аддитивного `SteamAppId` (Task 1). Позиционные вызовы конструктора `Game` в тестах должны компилироваться без правок.
- Комментарии в коде только там, где поведение неочевидно; хирургические правки, существующий стиль.
- `dotnet build` без предупреждений, `dotnet test` зелёный после каждой задачи.
- Визуальные проверки — только запуском из терминала пользователя (`run_in_terminal`), не из песочницы: она подменяет `%AppData%`.
- Тёмная тема: фон чистый чёрный `#000000`; акцент системный синий `#0A84FF`; цвет тумблера HDR `#30D158`. Шрифт `Segoe UI Variable`, откат `Segoe UI`.
- Существующие имена контролов сохраняются: `GamesGrid`, `RefreshButton`, `AddAppButton`, `AddToSteamButton`, `StatusText`, `RootsList`, `AddRootButton`, `RemoveRootButton`, `KeyBox`, `SteamUserBox`, `AutostartBox`, `TurnOffBox`.
- Коммит после каждой задачи, сообщение на английском, в конце строка `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

1. У Steam-игры нет папки в `librarycache` (игра ни разу не открывалась в клиенте): карточка показывает заглушку, исключений нет. Тест в Task 2 (`LocalPath_SteamWithoutCache_ReturnsNull`).
2. Файл обложки битый или нулевой длины: декодирование падает внутри, карточка получает заглушку, остальные карточки грузятся. Тест в Task 3 (`LoadImage_CorruptFile_ReturnsNull`).
3. Пересборка строк во время фоновых загрузок (пользователь нажал «Обновить»): одна и та же игра не качается дважды, результат попадает в актуальную строку. Тест в Task 3 (`FetchAsync_SameGameTwice_DownloadsOnce`).
4. Старый `library.json` без поля `SteamAppId`: читается, поле `null`, обложки Steam-игр появятся после фонового пересканирования. Тест в Task 1 (`Load_LibraryWithoutSteamAppId_Reads`).
5. Windows 10 или отключённые эффекты прозрачности: вызов DWM возвращает ошибку, окно получает сплошной фон темы и остаётся читаемым. Ручная проверка в Task 8 — исполнитель временно подменяет результат вызова на ошибку и смотрит окно.

---

### Task 1: SteamAppId у Game

**Files:**
- Modify: `src/metGameHDR/Scan/Game.cs`
- Modify: `src/metGameHDR/Scan/SteamSource.cs`
- Test: `tests/metGameHDR.Tests/Scan/SteamSourceTests.cs`, `tests/metGameHDR.Tests/Scan/LibraryTests.cs`

**Interfaces:**
- Produces: у записи `Game` появляется последний необязательный параметр `string? SteamAppId = null`. `SteamSource` заполняет его значением поля `appid` манифеста (строка как есть); если поля нет — `null`.

- [ ] **Step 1: Тест в `SteamSourceTests`** — `Scan_FillsSteamAppId`: два манифеста из существующего теста, проверка, что у `Half-Life` поле равно `"10"`, у `Portal` — `"20"`; манифест без `appid` даёт `null`.
- [ ] **Step 2: Запустить тест, убедиться, что не компилируется** (`dotnet test --filter Scan_FillsSteamAppId`).
- [ ] **Step 3: Добавить параметр в `Game` и заполнить его в `SteamSource`.**
- [ ] **Step 4: Тест в `LibraryTests`** — `Load_LibraryWithoutSteamAppId_Reads`: JSON с одной игрой без поля `SteamAppId` загружается, поле `null`.
- [ ] **Step 5: `dotnet build` без предупреждений, `dotnet test` зелёный.**
- [ ] **Step 6: Commit** — `Carry Steam appid on scanned games`.

### Task 2: Covers — поиск обложки на диске

**Files:**
- Create: `src/metGameHDR/Ui/Covers.cs`
- Test: `tests/metGameHDR.Tests/Ui/CoversTests.cs`

**Interfaces:**
- Consumes: `Game.SteamAppId` (Task 1), `ShortcutsFile.Find(VdfObject, string)` и `ShortcutsFile.AppIdOf(VdfObject)`.
- Produces: статический метод `Covers.LocalPath(Game game, string? steamPath, VdfObject? shortcuts, string? gridDir, string cacheDir)` возвращает путь к существующему файлу или `null`, в порядке спеки: `librarycache\<appid>\library_600x900.jpg`, затем `librarycache\<appid>_library_600x900.jpg`, затем `grid\<appid>p.*` для записей из `shortcuts`, затем файл в `cacheDir` с именем из `Covers.CacheFileName(string installDir)` и любым расширением. `CacheFileName` возвращает имя без расширения: недопустимые символы заменены на `_`, не длиннее 60 символов, в конце короткий хэш пути (8 hex), регистр пути не влияет на результат.

- [ ] **Step 1: Тесты в `CoversTests`** (временная папка на тест, как в `SteamSourceTests`):
  - `LocalPath_SteamNewLayout` — папка `appcache\librarycache\10\library_600x900.jpg` найдена.
  - `LocalPath_SteamOldLayout` — файл `appcache\librarycache\10_library_600x900.jpg` найден, когда новой раскладки нет.
  - `LocalPath_SteamWithoutCache_ReturnsNull` — Steam-игра без файлов даёт `null`.
  - `LocalPath_FolderAddedToSteam_UsesGrid` — после `ShortcutsFile.Upsert` в пустой корень и файла `<appid>p.png` в grid путь указывает на него.
  - `LocalPath_FallsBackToCache` — файл `cacheDir\<CacheFileName>.jpg` найден для Epic-игры.
  - `CacheFileName_SafeAndStable` — для `C:\Games\Foo: Bar?` нет символов `:\?`, длина ≤ 69, два вызова равны, пути в разном регистре дают одно имя, разные пути — разные имена.
- [ ] **Step 2: Запустить, убедиться, что не компилируется.**
- [ ] **Step 3: Реализовать `Covers.LocalPath` и `Covers.CacheFileName`** в `Ui/Covers.cs` (пространство имён `MetGameHDR.Ui`, класс пока статический, без WPF-типов, чтобы тесты шли без окна).
- [ ] **Step 4: `dotnet test` зелёный, `dotnet build` без предупреждений.**
- [ ] **Step 5: Commit** — `Resolve game cover paths from Steam caches and app cache`.

### Task 3: Covers — фоновая загрузка и картинка для карточки

**Files:**
- Modify: `src/metGameHDR/Ui/Covers.cs`
- Modify: `src/metGameHDR/Ui/GameRow.cs`
- Modify: `src/metGameHDR/AppPaths.cs` (свойство `CoversDir` — папка `covers` внутри `Dir`, создаётся при обращении)
- Test: `tests/metGameHDR.Tests/Ui/CoversTests.cs`

**Interfaces:**
- Consumes: `SteamGridDb(HttpClient, string)` с `SearchAsync(string)` и `BestImageUrlAsync("grids", id, "600x900")`; `Library.SearchNameOf(Game)`.
- Produces:
  - Экземплярный `Covers(HttpClient http, string cacheDir)` с методом `Task<string?> FetchAsync(Game game, string searchName, SteamGridDb db)`: качает лучшую обложку 600x900 в `cacheDir\<CacheFileName>.<ext>` (расширение из URL, по умолчанию `.png`), возвращает путь или `null`. Не более трёх загрузок одновременно (`SemaphoreSlim`). Повторный вызов для той же игры, пока первая загрузка идёт или уже завершилась неудачей, не делает новых запросов (словарь задач по `InstallDir`). Ошибки сети логируются через `Log.Write` и дают `null`.
  - Статический `ImageSource? Covers.LoadImage(string path)`: `BitmapImage` с `DecodePixelWidth = 300`, `CacheOption = OnLoad`, замороженный; при любой ошибке декодирования — `null`. Результат кэшируется в памяти по пути.
  - Статический `ImageSource? Covers.ExeIcon(string? exePath)`: иконка exe через `Icon.ExtractAssociatedIcon` → `BitmapSource`, замороженная; `null`, если exe нет или извлечь не удалось.
  - `GameRow` получает свойства `ImageSource? Cover` и `ImageSource? Icon` с `PropertyChanged`, и метод `void SetCover(ImageSource? cover)`. Заглушка — когда `Cover == null`: карточка показывает `Icon` и название.

- [ ] **Step 1: Тесты в `CoversTests`** с фейковым `HttpMessageHandler` по образцу `SteamGridDbTests`:
  - `FetchAsync_SavesBestGrid` — маршруты search и grids; после вызова в `cacheDir` лежит файл `<CacheFileName>.png` с байтами из ответа, метод вернул его путь.
  - `FetchAsync_NoMatch_ReturnsNull` — пустой `data` в search → `null`, файлов нет.
  - `FetchAsync_SameGameTwice_DownloadsOnce` — два параллельных вызова для одной игры, фейк считает запросы к search: ровно один.
  - `LoadImage_CorruptFile_ReturnsNull` — файл с байтами `1,2,3` → `null`, исключения нет.
- [ ] **Step 2: Запустить, убедиться, что не компилируется.**
- [ ] **Step 3: Реализовать `FetchAsync`, `LoadImage`, `ExeIcon`, `AppPaths.CoversDir`, свойства `GameRow`.** `LoadImage` трогает WPF-типы: замороженный `BitmapImage` создаётся из любого потока, тест должен проходить в обычном раннере без STA.
- [ ] **Step 4: `dotnet test` зелёный, `dotnet build` без предупреждений.**
- [ ] **Step 5: Commit** — `Download missing covers from SteamGridDB and expose cover images on rows`.

### Task 4: Тема — словари ресурсов и слежение за системной настройкой

**Files:**
- Create: `src/metGameHDR/Ui/Themes/Dark.xaml`, `src/metGameHDR/Ui/Themes/Light.xaml`
- Create: `src/metGameHDR/Ui/Theme.cs`
- Modify: `src/metGameHDR/App.xaml`, `src/metGameHDR/App.xaml.cs`

**Interfaces:**
- Produces:
  - Одинаковый набор ключей в обоих словарях (все `SolidColorBrush`, кроме `Overlay`): `WindowBackground` (под стекло, с альфой), `WindowBackgroundSolid` (откат без стекла), `Surface` (карточки, панели), `SurfaceHover`, `Border`, `Text`, `TextSecondary`, `Accent`, `AccentText`, `Hdr`, `Overlay` (`LinearGradientBrush` под названием на обложке). Тёмная: сплошной фон `#000000`, под стекло `#CC000000`, поверхности — белый с альфой 8–14 %. Светлая: сплошной `#F2F2F7`, под стекло `#CCF2F2F7`, поверхности белые.
  - `static class Theme` с `bool IsLight()` (реестр `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`, значение `AppsUseLightTheme`, отсутствие = светлая), `void Apply(Application app)` (заменяет первый словарь в `MergedDictionaries`; второй — стили из Task 5), `void Watch(Application app)` (подписка на `SystemEvents.UserPreferenceChanged`, категория `General`, вызывает `Apply` в диспетчере) и событие `event Action? Changed` (Task 6 обновляет по нему флаг тёмного заголовка).
- `App.xaml` подключает `Ui/Themes/Dark.xaml` как первый `MergedDictionary` по умолчанию; `App.OnStartup` до создания окна вызывает `Theme.Apply` и `Theme.Watch`.

- [ ] **Step 1: Создать оба словаря с одинаковыми ключами.** Проверка: списки `x:Key` из обоих файлов, отсортированные, совпадают.
- [ ] **Step 2: Реализовать `Theme`** и подключить в `App.xaml` / `App.xaml.cs`.
- [ ] **Step 3: `dotnet build` без предупреждений, `dotnet test` зелёный.**
- [ ] **Step 4: Commit** — `Add dark and light theme dictionaries that follow the Windows app theme`.

### Task 5: Стили контролов

**Files:**
- Create: `src/metGameHDR/Ui/Themes/Controls.xaml`
- Modify: `src/metGameHDR/App.xaml` (второй `MergedDictionary`)

**Interfaces:**
- Produces стили (все цвета через `DynamicResource` к ключам Task 4):
  - неявные: `Button` (скругление 8, высота 32, горизонтальный отступ 14, фон `Surface`, при наведении `SurfaceHover`, без системного `ButtonChrome`), `TextBox`, `ComboBox` (редактируемый и нет, скругление 8, фон `Surface`), `CheckBox` в виде тумблера (дорожка 40×22, бегунок 18, включено — `Accent`), `ListBox` и `ListBoxItem` прозрачные без рамок и системной подсветки выделения, `ScrollBar` тонкий 6 px без кнопок, `TextBlock` цвет `Text`, `Window` шрифт `Segoe UI Variable, Segoe UI` 13 px;
  - именованные: `SegmentButton` (`RadioButton`-пилюля: выбранный — фон `Surface`, текст `Text`; невыбранный — прозрачный, `TextSecondary`), `HdrToggle` (тумблер с цветом `Hdr` вместо `Accent`), `AccentButton` (фон `Accent`, текст `AccentText`), `CardSurface` (стиль `Border`: фон `Surface`, скругление 12, отступ 14) для настроек и панели деталей.
- [ ] **Step 1: Написать `Controls.xaml` и подключить в `App.xaml`.**
- [ ] **Step 2: `dotnet build` без предупреждений.** Ошибки ресурсов XAML проявляются только при запуске, поэтому дополнительно запустить приложение из терминала пользователя: старое окно открывается с новыми стилями кнопок, в `%AppData%\metGameHDR\log.txt` нет исключений.
- [ ] **Step 3: Commit** — `Style WPF controls for the OLED theme`.

### Task 6: MainWindow — карточки, панель деталей, стекло

**Files:**
- Modify: `src/metGameHDR/Ui/MainWindow.xaml`
- Modify: `src/metGameHDR/Ui/MainWindow.xaml.cs`
- Create: `src/metGameHDR/Ui/Backdrop.cs`

**Interfaces:**
- Consumes: `Covers` (Task 2–3), `GameRow.Cover`, `GameRow.Icon`, `GameRow.SetCover`, стили Task 5, `Theme.Changed`, `Theme.IsLight`.
- Produces:
  - `static class Backdrop` с `bool TryApply(Window window, bool dark)`: `DwmSetWindowAttribute` с атрибутом 38 (`DWMWA_SYSTEMBACKDROP_TYPE`) и значением 3 (`DWMSBT_TRANSIENTWINDOW`), затем атрибут 20 (`DWMWA_USE_IMMERSIVE_DARK_MODE`) по `dark`; `true`, когда оба вернули `S_OK`. Окно: `WindowChrome` с `GlassFrameThickness="-1"`, `UseAeroCaptionButtons="True"`, `CaptionHeight` 32; при успехе фон окна `WindowBackground`, при неудаче `WindowBackgroundSolid`.
  - Разметка по спеке §1. Шапка: два `SegmentButton` (`GamesTab` выбран по умолчанию, `SettingsTab`), страницы — два `Grid`, видимость привязана к `IsChecked` через `BooleanToVisibilityConverter`. `GamesGrid` — `ListBox` с `WrapPanel` в `ItemsPanelTemplate`, горизонтальная прокрутка отключена, `SelectionMode=Extended`. Шаблон карточки: `Border` 150×225 со скруглением 10, `Image` обложки `Stretch=UniformToFill`; при `Cover == null` — заглушка с `Icon` 48 px и названием; нижний градиент `Overlay` с названием; бейдж источника слева сверху; `HdrToggle` справа сверху с видимостью по `HdrApplies`; значок `✓` рядом с названием при непустом `SteamText`; выделение — рамка 2 px `Accent` по триггеру `IsSelected`. Панель деталей `DetailsPanel` (`CardSurface`, ширина 260) справа, `DataContext` — `GamesGrid.SelectedItem`, видима только при ровно одной выбранной карточке. Внизу — существующие кнопки и `StatusText`.
  - Страница настроек: четыре `CardSurface` по спеке §1 с теми же именами контролов; `AutostartBox` и `TurnOffBox` остаются `CheckBox` и получают неявный стиль-тумблер.
  - Правки `MainWindow.xaml.cs`: поле `covers` типа `Covers` на том же статическом `Http` и `AppPaths.CoversDir`; после `RebuildRows` вызывается `LoadCoversAsync()`: для каждой строки `Covers.LocalPath` с уже вычисленными `steam`, `shortcuts`, `gridDir`; найденное → `SetCover(LoadImage)`; иначе `Icon = ExeIcon(MainExe)` и при заданном ключе SteamGridDB — `FetchAsync` в фоне, после него `SetCover` в диспетчере, только если строка всё ещё в `rows`. Обработчик `SelectionChanged` переключает видимость панели. `Backdrop.TryApply` вызывается в `OnSourceInitialized` и повторно по `Theme.Changed`.
- [ ] **Step 1: Переписать `MainWindow.xaml`** по описанию, сохранив все имена контролов и обработчики.
- [ ] **Step 2: Добавить `Backdrop.cs` и правки `MainWindow.xaml.cs`.**
- [ ] **Step 3: `dotnet build` без предупреждений, `dotnet test` зелёный.**
- [ ] **Step 4: Запуск из терминала пользователя** (`dotnet run --project src/metGameHDR`): стекло видно; Steam-игры с обложками сразу; Epic и папки — заглушки, затем обложки из SteamGridDB при заданном ключе; клик по карточке открывает панель, Ctrl-клик выделяет несколько и панель скрывается; тумблер HDR пишет `library.json`; «Добавить выбранное в Steam» работает; страница настроек меняет значения как раньше; смена темы Windows на лету перекрашивает окно и заголовок; в `log.txt` нет ошибок.
- [ ] **Step 5: Commit** — `Rebuild the main window as a cover grid with a details panel and system backdrop`.

### Task 7: Иконка

**Files:**
- Modify: `tools/make-icon.ps1`
- Regenerate: `src/metGameHDR/assets/icon.ico`

- [ ] **Step 1: В скрипте фон `FromArgb(255, 0, 0, 0)`, высота RGB-полоски 8 px (ширина и положение прежние), остальное без изменений.**
- [ ] **Step 2: Перегенерировать:**
```bash
powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
```
- [ ] **Step 3: `git status` показывает изменённый `icon.ico`; собрать и убедиться, что иконка exe и трея обновилась.**
- [ ] **Step 4: Commit** — `Switch the app icon to a black OLED background`.

### Task 8: Документация и финальная проверка

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: В `CLAUDE.md`** дописать: в «Структура» — `Ui/Covers.cs`, `Ui/Theme.cs`, `Ui/Backdrop.cs`, `Ui/Themes/`; в «Данные» — папка `covers\` с обложками SteamGridDB для игр не из Steam; в «Неочевидное» — обложки Steam-игр берутся из `appcache\librarycache` клиента и есть только у игр, которые Steam успел закэшировать; тема читается из `AppsUseLightTheme` и меняется на лету; стекло через `DWMWA_SYSTEMBACKDROP_TYPE`, при ошибке сплошной фон.
- [ ] **Step 2: Полная проверка:**
```bash
dotnet build
```
```bash
dotnet test
```
Ожидание: `0 Warning(s)`, все тесты зелёные.
- [ ] **Step 3: Ручная проверка Review Focus 5:** временно заставить `Backdrop.TryApply` вернуть `false`, запустить из терминала пользователя, убедиться, что окно со сплошным фоном читаемо, откатить правку.
- [ ] **Step 4: `dotnet publish src/metGameHDR -c Release -o publish`** и запуск `publish\metGameHDR.exe` из терминала пользователя для одобрения вида Даниилом в тёмной и светлой теме.
- [ ] **Step 5: Commit** — `Document covers cache, theme following and backdrop fallback`.
