# metGameHDR

Трей-приложение (.NET 8 / WPF). Раз в 2 с смотрит процессы и включает системный HDR, пока запущена игра из библиотеки (Steam/Epic/GOG/Ubisoft/EA/Xbox/папки); выключает через 3 с после выхода, только если включило само. Также добавляет игры из папок и выбранные приложения в Steam как non-Steam ярлыки с обложками SteamGridDB.

## Структура
- `src/metGameHDR` (root namespace `MetGameHDR`): `Scan/` источники и `Library`, `Watch/` `ProcessWatcher`, `HdrController`, `DisplayConfigHdr`, `Steam/` текстовый и бинарный VDF, `ShortcutsFile`, `SteamGridDb`, `Artwork`, `SteamAdder`, `Ui/` окно, трей, автозапуск, `Covers` (поиск обложек + кэш загрузок SteamGridDB), `Theme` (системная тема), `Backdrop` (DWM-акрил), `Themes/Dark.xaml`, `Light.xaml`, `Controls.xaml` (стили контролов, все цвета через DynamicResource).
- `tests/metGameHDR.Tests` (xUnit), `tools/make-icon.ps1` (генерирует `src/metGameHDR/assets/icon.ico`; запускать с `-ExecutionPolicy Bypass`), спека и план в `docs/superpowers/`.
- Сторонних NuGet-пакетов кроме xUnit нет; комментарии в коде только там, где поведение неочевидно.

## Команды
- `dotnet build`, `dotnet test`
- `dotnet publish src/metGameHDR -c Release -o publish` — один exe, framework-dependent win-x64.
- Релиз: `git tag vX.Y.Z && git push origin vX.Y.Z` — `.github/workflows/release.yml` публикует два zip (framework-dependent и `-selfcontained`, версия из тега) и создаёт GitHub Release. CI (`ci.yml`) гоняет build и test на push/PR в master.
- `metGameHDR.exe --hdr status|on|off` — ручная проверка HDR; `--tray` — запуск без окна (так прописывается автозапуск в HKCU Run).

## Данные
`%AppData%\metGameHDR\`: `settings.json`, `library.json`, `log.txt` (после 1 МБ ротация в `log.old`). Битый JSON переименовывается в `.bad`, применяются значения по умолчанию.

## Неочевидное
- HDR переключается через DisplayConfig `GET_ADVANCED_COLOR_INFO_2` (type 15) / `SET_HDR_STATE` (type 16), на старых Windows откат на type 9/10: на 24H2+ старый API путает HDR с автоуправлением цветом.
- При записи в Steam обложки и иконки сначала качаются при работающем Steam (appid берётся из черновой копии `shortcuts.vdf`, которая не сохраняется); затем приложение само закрывает Steam (`steam.exe -shutdown`), перечитывает файл, пишет его атомарно с копией `.bak` и запускает Steam обратно. Одна запись на игру по папке игры; appid существующих записей сохраняется; `icon` и `LaunchOptions` существующей записи перезаписываются только непустым значением.
- Один экземпляр: мьютекс `Local\metGameHDR`; повторный запуск сигналит событием `Local\metGameHDR.Show`, первый экземпляр показывает окно, второй сразу выходит. `--hdr` идёт мимо мьютекса.
- Библиотека пересканируется при каждом старте в фоне (окно сразу показывает кэш), а также после изменения папок.
- В `Settings.Apps` хранится путь, выбранный пользователем, в том числе сам `.lnk`; ярлык разрешается при сканировании, его аргументы уходят в `LaunchOptions` записи Steam. Папки без exe в корне тоже считаются играми (`MainExe = null`).
- Пути папок игр нормализуются без завершающего `\` при сканировании — это общий ключ для дедупликации, настроек игр и поиска по процессу.
- Песочница Bash/PowerShell у Claude Code на этой машине подменяет `%AppData%` и блокирует смену настроек дисплея: реальные проверки HDR/Steam/автозапуска запускать через терминал пользователя (run_in_terminal), не из песочницы.
- Обложки Steam-игр берутся из собственного кэша клиента `appcache\librarycache\<appid>\library_600x900.jpg` (старая плоская раскладка тоже проверяется) — они есть только у игр, которые Steam успел закэшировать; для этого в `Game.SteamAppId` хранится appid из манифеста. Игры из папок/приложений, уже добавленные в Steam, берут `userdata\<user>\config\grid\<appid>p.*`. Загрузка из SteamGridDB — только при заданном ключе, не более 3 параллельно, неудачи запоминаются до перезапуска.
- Тема читается из `HKCU\...\Themes\Personalize\AppsUseLightTheme` и меняется на лету через `SystemEvents.UserPreferenceChanged`; чтобы стили перекрашивались, они обязаны использовать `DynamicResource`.
- Стекло: `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE=38, DWMSBT_TRANSIENTWINDOW=3)` + `DWMWA_USE_IMMERSIVE_DARK_MODE=20`; при ошибке (Windows 10) окно откатывается на сплошной фон темы.
- Неявный стиль `Window` не применяется к `MainWindow` (наследник), поэтому окно явно ставит `Style="{StaticResource {x:Type Window}}"`; неявный стиль `TextBlock` принудительно задаёт цвет текста внутри шаблонов, поэтому цвета бейджей/карточек задаются через Style. Бейджи карточек показывают только проблемные состояния (`nonHDR`, зачёркнутый `Steam`).
