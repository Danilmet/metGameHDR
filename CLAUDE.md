# metGameHDR

Трей-приложение (.NET 8 / WPF). Раз в 2 с смотрит процессы и включает системный HDR, пока запущена игра из библиотеки (Steam/Epic/GOG/Ubisoft/EA/Xbox/папки); выключает через 3 с после выхода, только если включило само. Также добавляет игры из папок и выбранные приложения в Steam как non-Steam ярлыки с обложками SteamGridDB.

## Структура
- `src/metGameHDR` (root namespace `MetGameHDR`): `Scan/` источники и `Library`, `Watch/` `ProcessWatcher`, `HdrController`, `DisplayConfigHdr`, `Steam/` текстовый и бинарный VDF, `ShortcutsFile`, `SteamGridDb`, `Artwork`, `SteamAdder`, `Ui/` окно, трей, автозапуск.
- `tests/metGameHDR.Tests` (xUnit), `tools/make-icon.ps1` (генерирует `src/metGameHDR/assets/icon.ico`; запускать с `-ExecutionPolicy Bypass`), спека и план в `docs/superpowers/`.
- Сторонних NuGet-пакетов кроме xUnit нет; комментарии в коде только там, где поведение неочевидно.

## Команды
- `dotnet build`, `dotnet test`
- `dotnet publish src/metGameHDR -c Release -o publish` — один exe, framework-dependent win-x64.
- `metGameHDR.exe --hdr status|on|off` — ручная проверка HDR; `--tray` — запуск без окна (так прописывается автозапуск в HKCU Run).

## Данные
`%AppData%\metGameHDR\`: `settings.json`, `library.json`, `log.txt` (после 1 МБ ротация в `log.old`). Битый JSON переименовывается в `.bad`, применяются значения по умолчанию.

## Неочевидное
- HDR переключается через DisplayConfig `GET_ADVANCED_COLOR_INFO_2` (type 15) / `SET_HDR_STATE` (type 16), на старых Windows откат на type 9/10: на 24H2+ старый API путает HDR с автоуправлением цветом.
- При записи в Steam приложение само закрывает Steam (`steam.exe -shutdown`), пишет `shortcuts.vdf` атомарно с копией `.bak` и запускает Steam обратно. Одна запись на игру по папке игры; appid существующих записей сохраняется.
- Песочница Bash/PowerShell у Claude Code на этой машине подменяет `%AppData%` и блокирует смену настроек дисплея: реальные проверки HDR/Steam/автозапуска запускать через терминал пользователя (run_in_terminal), не из песочницы.
