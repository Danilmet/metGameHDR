# metGameHDR — апдейтер

Статус: согласовано в обсуждении 2026-11-07.

## Цель

Приложение само узнаёт о новом релизе на GitHub, сообщает пользователю и по кнопке «Обновить» заменяет свой exe и перезапускается. Установщика нет, обновление — замена одного файла.

Не делаем: галочку «проверять обновления», показ текущей версии в заголовке, прогресс загрузки, pre-release и draft-релизы, пункт в трей-меню, тихую автоустановку.

Допущение: репозиторий `Danilmet/metGameHDR` публичный, GitHub Releases API доступен без токена (лимит 60 запросов в час — хватает с запасом).

## 1. Проверка

- Версия приложения — `Assembly.GetEntryAssembly().GetName().Version` (из `-p:Version` в `release.yml`; в Debug это `1.0.0.0`).
- Запрос: `GET https://api.github.com/repos/Danilmet/metGameHDR/releases/latest` с заголовком `User-Agent: metGameHDR` (без него GitHub отвечает 403). Endpoint `latest` сам исключает pre-release и draft.
- Чистая функция `Parse(json, current, selfContained)` → `Release(Version, AssetUrl, PageUrl)` или `null`: тег `vX.Y.Z` парсится в `Version`; результат есть только если версия строго больше текущей и среди `assets` найден нужный zip. Ассет выбирается по имени: `-win-x64-selfcontained.zip` при `selfContained`, иначе `-win-x64.zip` (не оканчивающийся на `-selfcontained.zip`). Битый JSON → `null`.
- Self-contained определяется как `typeof(object).Assembly.Location == ""`: в single-file self-contained рантайм лежит внутри бандла, во framework-dependent грузится из общей папки dotnet.
- Расписание: первая проверка через 10 с после старта, затем `DispatcherTimer` раз в 24 ч, пока приложение работает. Сетевые и HTTP-ошибки пишутся в лог и больше ничего не делают; следующая проверка по расписанию.

## 2. Уведомление

- Балун в трее «Доступна vX.Y.Z» один раз на версию (запоминается в памяти процесса, не в настройках).
- В окне, блок «Поведение»: скрытая до результата строка «Доступна vX.Y.Z» и кнопка «Обновить».
- Настройки и сохранённое состояние не добавляются.

## 3. Установка

1. Zip качается в `%AppData%\metGameHDR\update\` и распаковывается туда же (`System.IO.Compression.ZipFile`). В архиве ожидается `metGameHDR.exe`; его нет → ошибка.
2. `Swap(exe, newExe)`: текущий exe переименовывается в `metGameHDR.exe.old` (Windows разрешает переименовать запущенный exe), новый перемещается на его место. Если второй шаг не удался — `.old` возвращается на место, ошибка пробрасывается.
3. Перезапуск в `App`: стоп таймера слежения, `FinishHdr`, dispose трея, освобождение мьютекса `Local\metGameHDR` (иначе новый экземпляр сочтёт себя вторым и выйдет), `Process.Start(exe, "--tray" если окно скрыто)`, `Shutdown`.
4. При каждом старте удаляются `metGameHDR.exe.old` и папка `update`; ошибки удаления игнорируются.
5. Во время загрузки кнопка disabled с текстом «Загрузка…». Ошибка установки — текст в той же строке и запись в лог, кнопка снова активна.

## 4. Код

- Новый `src/metGameHDR/Update/Updater.cs`: `Current`, `IsSelfContained`, `Parse`, `CheckAsync(HttpClient)`, `DownloadAsync(Release, HttpClient) → путь к новому exe`, `Swap(exe, newExe)`, `Cleanup(exe)`.
- `App.xaml.cs`: вызов `Cleanup` при старте, таймеры проверки, `Restart()`, передача результата в трей и окно.
- `Tray.cs`: `ShowInfo(message)` рядом с `ShowError`.
- `MainWindow.xaml(.cs)`: строка и кнопка в блоке «Поведение», метод `ShowUpdate(Release, Func<Task> install)`.
- `CLAUDE.md`, «Неочевидное»: трюк с `.old`, освобождение мьютекса перед перезапуском, определение self-contained.

## 5. Тесты

`tests/metGameHDR.Tests/Update/UpdaterTests.cs`:
- `Parse`: версия новее → `Release` с нужным ассетом; равна или старше → `null`; `selfContained` выбирает `-selfcontained.zip`, иначе обычный; нет подходящего ассета → `null`; битый JSON → `null`.
- `Swap` на временных файлах: после вызова на месте exe новый файл, рядом `.old` со старым содержимым; если новый файл отсутствует, старый exe возвращён на место.
