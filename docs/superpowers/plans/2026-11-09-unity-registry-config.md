# Конфиги Unity в реестре — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Советчик по графике находит, читает, правит и откатывает настройки Unity-игр в `HKCU\Software\<Компания>\<Продукт>` (PlayerPrefs) так же, как INI/JSON-файлы.

**Architecture:** `ConfigPath` остаётся строкой: путь файла или `HKCU\Software\…`. Новый статический диспетчер `Advice/ConfigStore` ветвит все операции над конфигом по префиксу `HKCU\`: файловая ветка — перенос нынешнего кода `Advisor`, реестровая — новый `Advice/RegistryConfig` (чтение/запись через `Microsoft.Win32.Registry`, копии через `reg.exe`). Модель узнаёт о PlayerPrefs из подсказки `unityPlayerPrefs`, собранной из `<Игра>_Data\app.info`, и отвечает форматом `registry`.

**Tech Stack:** .NET 8 / WPF, `Microsoft.Win32.Registry` (в составе `net8.0-windows`), P/Invoke `advapi32!RegQueryInfoKeyW`, `reg.exe`, xUnit. Новых NuGet-пакетов нет.

**Spec:** `docs/superpowers/specs/2026-11-09-unity-registry-config-design.md`

## Global Constraints

- Сторонних NuGet-пакетов кроме xUnit нет; комментарии в коде только там, где поведение неочевидно (CLAUDE.md).
- Namespace тестов в `tests/metGameHUB.Tests/Advice` — `MetGameHUB.Tests` (не `.Advice`).
- Тесты с реестром работают только с ключом `HKCU\Software\metGameHUB.Tests\<guid>`, который сами создают и удаляют в `Dispose`. Копии `.reg` в тестах пишутся во временную папку, не в `%AppData%`.
- Поведение файловой ветки не меняется: существующие тесты `AdvisorTests`, `IniEditorTests` проходят без правок (кроме переименования вызовов, если плану это нужно).
- Песочница Claude Code может блокировать запись в HKCU и запуск `reg.exe`; если `dotnet test` падает только на реестровых тестах с ошибкой доступа, прогонять через терминал пользователя (`run_in_terminal`).
- Числа в выгрузке и разборе — инвариантная культура (`CultureInfo.InvariantCulture`).

## Review Focus

1. Кириллица в ключе и именах значений (компания «Студия», значение `Качество_h1`): выгрузка, запись, экспорт и имя файла копии не падают и не портят текст. → тест `Dump_And_Apply_CyrillicNames_RoundTrip` в задаче 3, `BackupPath_CyrillicSegments_SafeName` в задаче 4.
2. Модель вернула ключ в форме `HKEY_CURRENT_USER\Software\Co\Game\` (длинная форма, завершающий `\`, другой регистр): нормализуется в `HKCU\Software\Co\Game` и допускается. → `Normalize_LongForm_TrailingSlash` в задаче 2.
3. Имя без хэша совпадает с двумя значениями (`Width_h1`, `Width_h2` от разных версий Unity): правка отклоняется целиком, ничего не записано. → `Apply_AmbiguousPrefix_RefusesAll` в задаче 3.
4. Ключ исчез между поиском и применением (PlayerPrefs стёрты): `Apply` даёт `FileMissing`, без исключений. → `Apply_RegistryKeyMissing_FileMissing` в задаче 6.
5. Модель прислала `1920.0` для целого DWORD: правка отклоняется (`BadEdit`), ни одно значение ключа не изменено. → `Apply_DecimalForInt_RefusesAndWritesNothing` в задаче 3.

---

### Task 1: `ConfigStore` — файловая ветка (рефакторинг без смены поведения)

**Files:**
- Create: `src/metGameHUB/Advice/ConfigStore.cs`
- Modify: `src/metGameHUB/Advice/Advisor.cs` (`AskAsync` 52-70, `Prepare` 72-84, `ReadAsync` 109-121, `IsSnapshotFresh` 123-125, `TryFileText` 127-131, `CanWrite` 144-149, `Apply` 170-220, `Restore` 223-259, `ReadConfig` 270-284)
- Modify: `src/metGameHUB/Ui/GameRow.cs:253-254, 319-322, 335-340, 365-366`
- Modify: `src/metGameHUB/Ui/MainWindow.xaml.cs:624-632` (`ShowProvisionalFromFile`), `:690` («Файл не найден» → «Конфиг не найден»)
- Test: `tests/metGameHUB.Tests/Advice/AdvisorTests.cs` (существующие тесты — критерий)

**Interfaces:**
- Produces (`public static class ConfigStore`, namespace `MetGameHUB.Advice`):
  - `bool IsRegistry(string path)` — в этой задаче всегда `false` (префикс проверяется в задаче 5).
  - `bool Exists(string path)` — `File.Exists`.
  - `DateTime Stamp(string path)` — `File.GetLastWriteTimeUtc`.
  - `string? ReadText(string path)` — `File.ReadAllText`, `IOException`/`UnauthorizedAccessException` → null (бывший `Advisor.TryFileText`).
  - `bool CanWrite(string path, string text, IReadOnlyList<ConfigEdit> edits)` — бывший `Advisor.CanWrite(text, edits)`.
  - `bool Apply(string path, IReadOnlyList<ConfigEdit> edits)` — бывшее тело `Advisor.Apply` от чтения байтов до `File.Move` включительно: `.orig` (один раз), `.bak`, `IniEditor.ApplyBytes`, `.tmp` + `File.Move`; `ApplyBytes == null` → лог и `false`.
  - `bool HasBackup(string path)` — `File.Exists(path + ".orig")`; `bool WasApplied(string path)` — `File.Exists(path + ".bak")`.
  - `void Restore(string path)` — бывшее тело `Advisor.Restore` от копии в `.bak` до `File.Move`.
  - `string? CurrentValue(string path, string text, string? section, string key)` — `IniEditor.CurrentValue(text, section, key)`.
- `Advisor.CanWrite(string text, edits)` остаётся как тонкая обёртка над `ConfigStore.CanWrite(path: "", …)` только если на неё есть тесты (`grep CanWrite tests/`); иначе удаляется, а вызов в `GameRow.RefreshCanApply` переводится на `ConfigStore.CanWrite(path, text, edits)`.

- [ ] **Step 1: Прогнать тесты до правок**

Run: `dotnet test tests/metGameHUB.Tests --filter "FullyQualifiedName~Advisor|FullyQualifiedName~IniEditor"`
Expected: все PASS (это базовая линия).

- [ ] **Step 2: Создать `ConfigStore.cs` с сигнатурами из Interfaces, перенеся код из `Advisor`**

`Apply` для файла: `original = File.ReadAllBytes(path)`; `ApplyBytes == null` → `Log.Write($"Apply advice: value does not fit the ANSI encoding of {path}")`, `return false`; дальше `.orig`/`.bak`/`.tmp`/`File.Move` как сейчас.

- [ ] **Step 3: Переписать `Advisor` на `ConfigStore`**

- `Prepare`: `stamp = path == null ? default : ConfigStore.Stamp(path)`.
- `ReadAsync`: `TryFileText(path!)` → `ConfigStore.ReadText(path!)`.
- `IsSnapshotFresh`: `ConfigStore.Exists(p) && ConfigStore.Stamp(p) == s.FileStamp`.
- `Apply`: `FileMissing`, если `path == null || !ConfigStore.Exists(path)`; затем `EditsValid`, `isRunning`; `text = ConfigStore.ReadText(path)` (null → `FileMissing`); `!ConfigStore.CanWrite(path, text, edits)` → `NotIni`; `!ConfigStore.Apply(path, edits)` → `BadEdit`; `FileStamp = freshBefore ? ConfigStore.Stamp(path) : snap.FileStamp`.
- `Restore`: `path == null || !ConfigStore.HasBackup(path)` → `FileMissing`; `ConfigStore.Restore(path)`; `text = ConfigStore.ReadText(path) ?? ""`; `IniEditor.CurrentValue(text, …)` → `ConfigStore.CurrentValue(path, text, …)`.
- `ReadConfig`: `if (path == null || !ConfigStore.Exists(path)) return null; var raw = ConfigStore.ReadText(path); if (raw == null) return null;` обрезка до `MaxConfigChars` с `Truncated = raw.Length > MaxConfigChars`, затем `MaskSecrets`; формат как сейчас по `{`.
- Удалить `TryFileText`; `CanWrite` — по правилу из Interfaces.

- [ ] **Step 4: Переписать UI**

`GameRow.cs`: `File.Exists(p)` → `ConfigStore.Exists(p)` (строки 254, 321, 338, 366); `File.ReadAllText(path)` в `RefreshCanApply` → `ConfigStore.ReadText(path) is { } text && ConfigStore.CanWrite(path, text, edits)`; `File.Exists(path + ".orig")` → `ConfigStore.HasBackup(path)`; `".bak"` → `ConfigStore.WasApplied(path)`. `MainWindow.ShowProvisionalFromFile`: `if (ConfigStore.ReadText(path) is not { } text) return; var rows = UnrealScalability.FromFile(text);`. Строка 690: `"Конфиг не найден"`.

- [ ] **Step 5: Собрать и прогнать тесты**

Run: `dotnet build && dotnet test tests/metGameHUB.Tests`
Expected: 0 warnings новых, все PASS.

- [ ] **Step 6: Commit**

```bash
git add src/metGameHUB/Advice/ConfigStore.cs src/metGameHUB/Advice/Advisor.cs src/metGameHUB/Ui/GameRow.cs src/metGameHUB/Ui/MainWindow.xaml.cs
git commit -m "Advisor: route config file access through ConfigStore"
```

---

### Task 2: `RegistryConfig` — ключ, допуск, существование, время записи, выгрузка

**Files:**
- Create: `src/metGameHUB/Advice/RegistryConfig.cs`
- Test: `tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs`

**Interfaces:**
- Produces (`public static class RegistryConfig`, namespace `MetGameHUB.Advice`):
  - `string Normalize(string key)` — `HKEY_CURRENT_USER\` → `HKCU\` (без учёта регистра), обрезка завершающих `\` и `/`, `/` → `\`.
  - `bool IsAllowedKey(string key)` — после `Normalize`: начинается с `HKCU\Software\`, под `Software` ≥ 2 сегментов, первый сегмент не `Microsoft`, `Classes`, `Policies`, `Wow6432Node` (без учёта регистра).
  - `string SubKey(string key)` — часть после `HKCU\` (для `Registry.CurrentUser.OpenSubKey`).
  - `bool Exists(string key)`.
  - `DateTime Stamp(string key)` — время последней записи ключа (UTC); несуществующий ключ → `default`.
  - `string Dump(string key)` — строки `имя=значение`, `\n`, отсортированы `StringComparer.OrdinalIgnoreCase`; несуществующий ключ → `""`.
  - `internal enum ValueKind { None, Int, Float, BinaryString, Sz }` и `internal static ValueKind Classify(object? data, RegistryValueKind kind)` — по таблице спеки: DWORD → `Float`, если `unchecked((uint)(int)data) > 1_000_000` и `float.IsFinite(BitConverter.Int32BitsToSingle((int)data))`, иначе `Int`; `Binary` с валидным UTF-8 (строгий `UTF8Encoding(false, true)`) → `BinaryString`; `String` → `Sz`; прочее → `None`.
  - `internal static string? Format(object? data, RegistryValueKind kind)` — текст по классификации: `Int` → `((int)data).ToString(Invariant)`; `Float` → `BitConverter.Int32BitsToSingle(...).ToString("R", Invariant)`; `BinaryString` → UTF-8 без завершающих `\0`; `Sz` → строка; `None` → null.
- P/Invoke (точная форма):

```csharp
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
static extern int RegQueryInfoKeyW(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
    IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
    IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);
// Stamp: key.Handle → RegQueryInfoKeyW(..., out ft) == 0 ? DateTime.FromFileTimeUtc(ft) : default
```

- [ ] **Step 1: Написать падающие тесты**

Фикстура `sealed class TestKey : IDisposable` в `RegistryConfigTests.cs`: `public string Key = @"HKCU\Software\metGameHUB.Tests\" + Guid.NewGuid().ToString("N");`, `public RegistryKey Reg = Registry.CurrentUser.CreateSubKey(...)`, `Dispose` → `Registry.CurrentUser.DeleteSubKeyTree(sub, false)`. Хелперы: `Dword(name, int)`, `FloatBits(name, float)` (`BitConverter.SingleToInt32Bits`, kind `DWord`), `Bin(name, string)` (`UTF8 + "\0"`, kind `Binary`), `Sz(name, string)`.

```csharp
[Fact] public void Normalize_LongForm_TrailingSlash() =>
    Assert.Equal(@"HKCU\Software\Co\Game", RegistryConfig.Normalize(@"hkey_current_user\Software\Co\Game\"));

[Theory]
[InlineData(@"HKCU\Software\Co\Game", true)]
[InlineData(@"HKEY_CURRENT_USER\Software\Co\Game\Sub", true)]
[InlineData(@"HKCU\Software\Co", false)]
[InlineData(@"HKCU\Software\Microsoft\Windows", false)]
[InlineData(@"HKCU\Software\Wow6432Node\Co\Game", false)]
[InlineData(@"HKLM\Software\Co\Game", false)]
[InlineData(@"C:\Games\a.ini", false)]
public void IsAllowedKey(string key, bool expected) => Assert.Equal(expected, RegistryConfig.IsAllowedKey(key));

[Fact] public void Exists_And_Stamp()
{
    using var k = new TestKey();
    Assert.True(RegistryConfig.Exists(k.Key));
    Assert.False(RegistryConfig.Exists(k.Key + "x"));
    Assert.Equal(default, RegistryConfig.Stamp(k.Key + "x"));
    var before = DateTime.UtcNow.AddSeconds(-5);
    k.Dword("A_h1", 1);
    Assert.InRange(RegistryConfig.Stamp(k.Key), before, DateTime.UtcNow.AddSeconds(5));
}

[Fact] public void Dump_FormatsTypesSortedAndSkipsUnknown()
{
    using var k = new TestKey();
    k.Dword("Screenmanager Resolution Width_h182942802", 1920);
    k.Dword("UnitySelectMonitor_h17969598", -1);
    k.FloatBits("Volume_h3", 0.75f);
    k.Bin("Lang_h4", "ru");
    k.Sz("Plain", "x");
    k.Reg.SetValue("Q", 5L, RegistryValueKind.QWord);
    k.Reg.SetValue("Raw", new byte[] { 0xFF, 0xFE }, RegistryValueKind.Binary);
    Assert.Equal("Lang_h4=ru\nPlain=x\nScreenmanager Resolution Width_h182942802=1920\nUnitySelectMonitor_h17969598=-1\nVolume_h3=0.75",
        RegistryConfig.Dump(k.Key));
}

[Fact] public void Dump_MissingKey_Empty() => Assert.Equal("", RegistryConfig.Dump(@"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N")));
```

- [ ] **Step 2: Убедиться, что тесты не компилируются / падают**

Run: `dotnet test tests/metGameHUB.Tests --filter "FullyQualifiedName~RegistryConfig"`
Expected: ошибка компиляции «RegistryConfig не найден».

- [ ] **Step 3: Реализовать `RegistryConfig.cs` по Interfaces**

`Dump` перебирает `GetValueNames()`, берёт `GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)` и `GetValueKind(name)`, строки `Format != null`.

- [ ] **Step 4: Прогнать тесты**

Run: `dotnet test tests/metGameHUB.Tests --filter "FullyQualifiedName~RegistryConfig"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/metGameHUB/Advice/RegistryConfig.cs tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs
git commit -m "RegistryConfig: key validation, stamp and dump of PlayerPrefs"
```

---

### Task 3: `RegistryConfig.Apply` и `CurrentValue`

**Files:**
- Modify: `src/metGameHUB/Advice/RegistryConfig.cs`
- Test: `tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs`

**Interfaces:**
- Consumes: `ConfigEdit(string? Section, string Key, string Value)` из `Models.cs`; `Classify`, `Format` из задачи 2.
- Produces:
  - `bool Apply(string key, IReadOnlyList<ConfigEdit> edits)` — `false`, если ключа нет, любое имя не разрешается, разрешается неоднозначно, значение не парсится или тип `None`; иначе пишет все и `true`. Запись начинается только после проверки всех правок.
  - `string? CurrentValue(string key, string name)` — `Format` разрешённого значения или null.
  - `internal static string? ResolveName(IReadOnlyList<string> names, string key)` — точное совпадение (OrdinalIgnoreCase), иначе единственное имя, у которого `Regex.Replace(name, @"_h\d+$", "")` совпадает с `key` (OrdinalIgnoreCase); иначе null.
  - `internal static (object Data, RegistryValueKind Kind)? Convert(string value, ValueKind kind)` — `Int` → `int.Parse(value, NumberStyles.Integer, Invariant)` / `DWord`; `Float` → `BitConverter.SingleToInt32Bits(float.Parse(value, NumberStyles.Float, Invariant))` / `DWord`; `BinaryString` → `UTF8.GetBytes(value + "\0")` / `Binary`; `Sz` → `value` / `String`; ошибка парсинга или `None` → null.

- [ ] **Step 1: Написать падающие тесты**

```csharp
[Fact] public void Apply_KeepsTypes()
{
    using var k = new TestKey();
    k.Dword("Width_h1", 1920); k.FloatBits("Vol_h2", 0.5f); k.Bin("Lang_h3", "ru"); k.Sz("Plain", "x");
    Assert.True(RegistryConfig.Apply(k.Key, [new(null, "Width_h1", "2560"), new(null, "Vol_h2", "0.25"), new(null, "Lang_h3", "en"), new(null, "Plain", "y")]));
    Assert.Equal(2560, k.Reg.GetValue("Width_h1"));
    Assert.Equal(RegistryValueKind.DWord, k.Reg.GetValueKind("Vol_h2"));
    Assert.Equal(0.25f, BitConverter.Int32BitsToSingle((int)k.Reg.GetValue("Vol_h2")!));
    Assert.Equal("en\0"u8.ToArray(), k.Reg.GetValue("Lang_h3"));
    Assert.Equal("y", k.Reg.GetValue("Plain"));
}

[Fact] public void Apply_MatchesNameWithoutHash()
{
    using var k = new TestKey();
    k.Dword("Screenmanager Fullscreen mode_h3630240806", 1);
    Assert.True(RegistryConfig.Apply(k.Key, [new(null, "screenmanager fullscreen mode", "3")]));
    Assert.Equal(3, k.Reg.GetValue("Screenmanager Fullscreen mode_h3630240806"));
}

[Fact] public void Apply_AmbiguousPrefix_RefusesAll()
{
    using var k = new TestKey();
    k.Dword("Width_h1", 1); k.Dword("Width_h2", 2); k.Dword("Other_h3", 3);
    Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Other_h3", "9"), new(null, "Width", "5")]));
    Assert.Equal(3, k.Reg.GetValue("Other_h3"));
}

[Fact] public void Apply_UnknownName_False()
{
    using var k = new TestKey(); k.Dword("A_h1", 1);
    Assert.False(RegistryConfig.Apply(k.Key, [new(null, "B_h2", "1")]));
}

[Fact] public void Apply_DecimalForInt_RefusesAndWritesNothing()
{
    using var k = new TestKey(); k.Dword("Width_h1", 1920); k.Dword("Height_h2", 1080);
    Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Height_h2", "1440"), new(null, "Width_h1", "1920.0")]));
    Assert.Equal(1080, k.Reg.GetValue("Height_h2"));
}

[Fact] public void Apply_UnreadableBinary_False()
{
    using var k = new TestKey(); k.Reg.SetValue("Raw_h1", new byte[] { 0xFF, 0xFE }, RegistryValueKind.Binary);
    Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Raw_h1", "x")]));
}

[Fact] public void Apply_MissingKey_False() =>
    Assert.False(RegistryConfig.Apply(@"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N"), [new(null, "A", "1")]));

[Fact] public void Dump_And_Apply_CyrillicNames_RoundTrip()
{
    using var k = new TestKey(); k.Bin("Качество_h1", "Высокое");
    Assert.Equal("Качество_h1=Высокое", RegistryConfig.Dump(k.Key));
    Assert.True(RegistryConfig.Apply(k.Key, [new(null, "Качество", "Низкое")]));
    Assert.Equal("Низкое", RegistryConfig.CurrentValue(k.Key, "Качество_h1"));
}

[Fact] public void CurrentValue_FloatAndMissing()
{
    using var k = new TestKey(); k.FloatBits("Vol_h2", 0.75f);
    Assert.Equal("0.75", RegistryConfig.CurrentValue(k.Key, "Vol"));
    Assert.Null(RegistryConfig.CurrentValue(k.Key, "Nope"));
}
```

- [ ] **Step 2: Убедиться, что тесты падают** — Run: тот же фильтр; Expected: ошибка компиляции на `Apply`/`CurrentValue`.

- [ ] **Step 3: Реализовать `ResolveName`, `Convert`, `Apply`, `CurrentValue`**

`Apply`: открыть `OpenSubKey(sub, writable: true)` (null → false); собрать список `(name, data, kind)` для всех правок, при первом провале вернуть `false`; затем `SetValue` по списку.

- [ ] **Step 4: Прогнать тесты** — Run: `dotnet test … --filter "FullyQualifiedName~RegistryConfig"`; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/metGameHUB/Advice/RegistryConfig.cs tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs
git commit -m "RegistryConfig: typed writes by exact or hash-less value name"
```

---

### Task 4: Копия и откат через `reg.exe`

**Files:**
- Modify: `src/metGameHUB/Advice/RegistryConfig.cs`
- Modify: `src/metGameHUB/AppPaths.cs:39` (добавить `RegistryDir` рядом с `UpdateDir`, с `Directory.CreateDirectory` как у `CoversDir`)
- Test: `tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs`

**Interfaces:**
- Produces:
  - `string BackupFileName(string key)` — последние два сегмента нормализованного ключа, недопустимые символы файла → `_`, соединены `_`, обрезка до 60 символов, плюс `-` и 8 hex символов (нижний регистр) `SHA256` от `Normalize(key).ToUpperInvariant()` (как `Covers.CacheFileName`). Без расширения: `Co_Game-1a2b3c4d`.
  - `void Backup(string key, string file)` — `reg export "HKEY_CURRENT_USER\<SubKey>" "<file>" /y`.
  - `void Restore(string file)` — `reg import "<file>"`.
  - Оба: `Process` из `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe")`, `UseShellExecute=false`, `CreateNoWindow=true`, `RedirectStandardError=true`, `RedirectStandardOutput=true`; код возврата ≠ 0 → `throw new IOException($"reg {verb}: {stderr.Trim()}")` (stdout читать тоже, чтобы не блокировать).
  - `AppPaths.RegistryDir` → `%AppData%\metGameHUB\registry\` (создаётся).

- [ ] **Step 1: Написать падающие тесты**

```csharp
[Fact] public void BackupFileName_LastTwoSegmentsAndHash()
{
    var name = RegistryConfig.BackupFileName(@"HKEY_CURRENT_USER\Software\Co\Game\");
    Assert.Matches(@"^Co_Game-[0-9a-f]{8}$", name);
    Assert.Equal(name, RegistryConfig.BackupFileName(@"HKCU\Software\Co\Game"));
}

[Fact] public void BackupPath_CyrillicSegments_SafeName()
{
    var name = RegistryConfig.BackupFileName(@"HKCU\Software\Студия\Игра: Два");
    Assert.StartsWith("Студия_Игра_ Два-", name);
    Assert.DoesNotContain(':', name);
}

[Fact] public void Backup_Then_Restore_RoundTrip()
{
    using var k = new TestKey(); k.Dword("Width_h1", 1920);
    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg");
    try
    {
        RegistryConfig.Backup(k.Key, file);
        Assert.True(File.Exists(file));
        k.Reg.SetValue("Width_h1", 2560, RegistryValueKind.DWord);
        RegistryConfig.Restore(file);
        Assert.Equal(1920, Registry.CurrentUser.OpenSubKey(RegistryConfig.SubKey(k.Key))!.GetValue("Width_h1"));
    }
    finally { File.Delete(file); }
}

[Fact] public void Restore_MissingFile_Throws() =>
    Assert.Throws<IOException>(() => RegistryConfig.Restore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg")));
```

- [ ] **Step 2: Убедиться, что тесты падают** — Run: фильтр `RegistryConfig`; Expected: ошибка компиляции.

- [ ] **Step 3: Реализовать `BackupFileName`, `Backup`, `Restore`, `AppPaths.RegistryDir`**

- [ ] **Step 4: Прогнать тесты** — Expected: PASS. Если `Backup_Then_Restore_RoundTrip` падает с отказом доступа к `reg.exe`/HKCU в песочнице — прогнать через терминал пользователя и зафиксировать это в отчёте.

- [ ] **Step 5: Commit**

```bash
git add src/metGameHUB/Advice/RegistryConfig.cs src/metGameHUB/AppPaths.cs tests/metGameHUB.Tests/Advice/RegistryConfigTests.cs
git commit -m "RegistryConfig: .reg backup and restore via reg.exe"
```

---

### Task 5: Подсказка `unityPlayerPrefs` и формат `registry` в запросах

**Files:**
- Modify: `src/metGameHUB/Advice/ConfigFinder.cs` (рядом с `UnrealProject`, :115)
- Modify: `src/metGameHUB/Advice/ClaudeClient.cs:9` (`LocateRequest`), `:40-48` (`LocatePrompt`), `:57` и `:70` (правило 5 `AskPrompt`/`AdjustPrompt`), `:84` (правило 3 `ReadPrompt`), `:144-150` (`BuildLocateBody`)
- Modify: `src/metGameHUB/Advice/Models.cs:13` (комментарий `Format: "ini" | "json" | "registry" | "none"`)
- Modify: `src/metGameHUB/Advice/Advisor.cs` (`LocateAsync`: передать `ConfigFinder.UnityPlayerPrefsKey(effective.InstallDir)`)
- Test: `tests/metGameHUB.Tests/Advice/ConfigFinderTests.cs`, `tests/metGameHUB.Tests/Advice/ClaudeClientTests.cs`

**Interfaces:**
- Produces:
  - `string? ConfigFinder.UnityPlayerPrefsKey(string installDir)` — первая по имени папка `*_Data` в `installDir` с файлом `app.info`; первые две непустые строки (trim) → `$@"HKCU\Software\{company}\{product}"`; иначе null; `IOException`/`UnauthorizedAccessException` → null.
  - `LocateRequest(Game Game, IReadOnlyList<string> InstallRootEntries, IReadOnlyList<string> CandidatePaths, string? UnrealProject, string? UnityKey)`; в теле поиска поле `unityPlayerPrefs = r.UnityKey`.
- Точный текст промптов:
  - `LocatePrompt`, правило 1, начало: `1. Назови ОДИН файл или ключ реестра, в котором игра хранит графические настройки меню …` (остальное без изменений).
  - `LocatePrompt`, правило 2 целиком: `2. format: "ini" для INI/CFG с парами ключ=значение или ключ значение; "json" для JSON; "registry", если игра на Unity хранит настройки в PlayerPrefs — тогда path это ключ реестра HKCU\Software\<Компания>\<Продукт> (если прислан unityPlayerPrefs и графика игры лежит в PlayerPrefs, верни его как есть); "none", если настройки в бинарном файле или в облаке — тогда path: null.`
  - Схема `LocatePrompt`: `"enum":["ini","json","registry","none"]`.
  - `AskPrompt` и `AdjustPrompt`, в конец правила 5: ` Для format "registry": section всегда null, key — точное имя значения из выгрузки вместе с хвостом _h…, value — число или строка в том виде, как в выгрузке.`
  - `ReadPrompt`, в конец правила 3: ` Для format "registry": section всегда null, key — точное имя значения из выгрузки вместе с хвостом _h….`

- [ ] **Step 1: Написать падающие тесты**

`ConfigFinderTests`:
```csharp
[Fact] public void UnityPlayerPrefsKey_FromAppInfo()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(dir, "Game_Data"));
    File.WriteAllText(Path.Combine(dir, "Game_Data", "app.info"), "Studio Co\nMy Game\n");
    try { Assert.Equal(@"HKCU\Software\Studio Co\My Game", ConfigFinder.UnityPlayerPrefsKey(dir)); }
    finally { Directory.Delete(dir, true); }
}

[Fact] public void UnityPlayerPrefsKey_NoAppInfo_Null()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try { Assert.Null(ConfigFinder.UnityPlayerPrefsKey(dir)); }
    finally { Directory.Delete(dir, true); }
}
```
`ClaudeClientTests` (по образцу `LocateAsync_SendsGameListingAndCandidates_ParsesLocation`):
```csharp
[Fact] public void BuildLocateBody_HasUnityPlayerPrefs()
{
    var body = ClaudeClient.BuildLocateBody(new LocateRequest(game, [], [], null, @"HKCU\Software\Co\Game"), "m");
    // разобрать messages[0].content как JSON и проверить:
    Assert.Equal(@"HKCU\Software\Co\Game", user.GetProperty("unityPlayerPrefs").GetString());
    Assert.Contains("\"registry\"", body); // enum схемы в system
}

[Fact] public async Task LocateAsync_RegistryFormat_Parsed()  // ответ {"path":"HKCU\\Software\\Co\\Game","format":"registry","note":"n"} → Format == "registry", Path == ключ
```

- [ ] **Step 2: Убедиться, что тесты падают** — Run: `dotnet test … --filter "FullyQualifiedName~ConfigFinder|FullyQualifiedName~ClaudeClient"`; Expected: ошибка компиляции (`UnityPlayerPrefsKey`, пятый параметр `LocateRequest`).

- [ ] **Step 3: Реализовать** — `UnityPlayerPrefsKey`, параметр `UnityKey`, поле `unityPlayerPrefs`, тексты промптов, вызов в `Advisor.LocateAsync`. Починить существующие вызовы `new LocateRequest(...)` в тестах (добавить `null`).

- [ ] **Step 4: Прогнать тесты** — Run: `dotnet test tests/metGameHUB.Tests`; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/metGameHUB/Advice tests/metGameHUB.Tests/Advice
git commit -m "Advisor: hint Unity PlayerPrefs key and accept registry format"
```

---

### Task 6: Реестровая ветка `ConfigStore` и `Advisor`

**Files:**
- Modify: `src/metGameHUB/Advice/ConfigStore.cs` (все методы задачи 1 получают ветку `IsRegistry`)
- Modify: `src/metGameHUB/Advice/Advisor.cs` (`LocateAsync` 27-45, `LocatedPath` 265-267, `ReadConfig`)
- Test: `tests/metGameHUB.Tests/Advice/AdvisorTests.cs`

**Interfaces:**
- Consumes: `RegistryConfig.*` из задач 2-4, `AppPaths.RegistryDir`.
- Produces (`ConfigStore`):
  - `IsRegistry(path)` — `path.StartsWith(@"HKCU\", OrdinalIgnoreCase) || path.StartsWith(@"HKEY_CURRENT_USER\", OrdinalIgnoreCase)`.
  - `internal static Func<string> BackupDir = () => AppPaths.RegistryDir;` — тесты подменяют на временную папку.
  - Реестровая ветка: `Exists` → `RegistryConfig.Exists`; `Stamp` → `RegistryConfig.Stamp`; `ReadText` → `RegistryConfig.Dump`; `CanWrite` → `true`; `Apply` → `Backup(key, Orig)` если `!File.Exists(Orig)`, `Backup(key, Bak)`, `RegistryConfig.Apply`; `HasBackup` → `File.Exists(Orig)`; `WasApplied` → `File.Exists(Bak)`; `Restore` → `Backup(key, Bak)`, `RegistryConfig.Restore(Orig)`; `CurrentValue(path, text, section, key)` → `RegistryConfig.CurrentValue(path, key)`; где `Orig = Path.Combine(BackupDir(), BackupFileName(key) + ".orig.reg")`, `Bak = … + ".bak.reg"`.
- `Advisor.LocatedPath`: если `o.ConfigPath` — реестр (`ConfigStore.IsRegistry`), вернуть `RegistryConfig.Normalize(p)` при `IsAllowedKey(p)`, иначе null; файловая ветка как сейчас.
- `Advisor.LocateAsync`: `Format == "registry"` → `path = location.Path != null && RegistryConfig.IsAllowedKey(location.Path) ? RegistryConfig.Normalize(location.Path) : null`, при null та же заметка «Модель указала путь вне разрешённых папок»; `Format is "ini" or "json"` — как сейчас; `ConfigStatus(path, ConfigStore.Exists(path), location.Format, note)`.
- `Advisor.ReadConfig`: формат `ConfigStore.IsRegistry(path) ? "registry" : (по '{')`.

- [ ] **Step 1: Написать падающие тесты в `AdvisorTests`**

В `Env`: поле `public string BackupDir = Path.Combine(Dir, "regbak")`, в конструкторе `Directory.CreateDirectory(BackupDir); ConfigStore.BackupDir = () => BackupDir;`. Хелпер `RegKey()` — создаёт `HKCU\Software\metGameHUB.Tests\<guid>` с `Width_h1 = 1920` (DWORD), удаляет в `Dispose`; `Lib.For(Game).ConfigPath = key`.

```csharp
[Fact] public async Task LocateAsync_Registry_AllowedKeyStored()
{
    using var e = new Env();
    var status = await e.Advisor(Locate(@"HKEY_CURRENT_USER\Software\Co\Game\", "registry")).LocateAsync(e.Game, default);
    Assert.Equal(@"HKCU\Software\Co\Game", status.Path);
    Assert.Equal("registry", status.Format);
    Assert.False(status.Exists);
    Assert.Equal(@"HKCU\Software\Co\Game", e.Lib.For(e.Game).ConfigPath);
}

[Fact] public async Task LocateAsync_Registry_MicrosoftKey_Rejected()
{
    using var e = new Env();
    var status = await e.Advisor(Locate(@"HKCU\Software\Microsoft\Windows", "registry")).LocateAsync(e.Game, default);
    Assert.Null(status.Path);
    Assert.Null(e.Lib.For(e.Game).ConfigPath);
    Assert.Equal("Модель указала путь вне разрешённых папок", e.Lib.For(e.Game).ConfigNote);
}

[Fact] public async Task AskAsync_RegistryKey_SendsDumpAsRegistry()
{
    using var e = new Env(); e.RegKey();
    await e.Advisor(Reply(AskJson)).AskAsync(e.Game, GoalStep.Balanced, default);
    var cfg = SentConfig(e.Handler!.Body);
    Assert.Equal("registry", cfg.GetProperty("format").GetString());
    Assert.Equal("Width_h1=1920", cfg.GetProperty("text").GetString());
}

[Fact] public void Apply_RegistryKey_WritesAndBacksUp_ThenRestore()
{
    using var e = new Env(); var key = e.RegKey();
    var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", "1920", "2560", null, "Width_h1", "2560", "r")], null), DateTime.UtcNow);
    Assert.Equal(ApplyOutcome.Applied, e.Advisor().Apply(e.Game, advice));
    Assert.Equal(2560, e.RegValue("Width_h1"));
    Assert.Single(Directory.GetFiles(e.BackupDir, "*.orig.reg"));
    Assert.Single(Directory.GetFiles(e.BackupDir, "*.bak.reg"));
    Assert.Equal("2560", e.Lib.For(e.Game).Advice!.Result.Settings[0].Current);
    Assert.Equal(ApplyOutcome.Applied, e.Advisor().Restore(e.Game));
    Assert.Equal(1920, e.RegValue("Width_h1"));
    Assert.Equal("1920", e.Lib.For(e.Game).Advice!.Result.Settings[0].Current);
}

[Fact] public void Apply_RegistryKeyMissing_FileMissing()
{
    using var e = new Env();
    e.Lib.For(e.Game).ConfigPath = @"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N");
    Assert.Equal(ApplyOutcome.FileMissing, e.Advisor().Apply(e.Game, new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", null, "1", null, "Width_h1", "1", "r")], null), DateTime.UtcNow)));
}

[Fact] public void Apply_RegistryBadName_BadEdit()
{
    using var e = new Env(); e.RegKey();
    var advice = new Advice.Advice(new AdviceResult("s", [new SettingAdvice("W", null, "1", null, "Nope_h9", "1", "r")], null), DateTime.UtcNow);
    Assert.Equal(ApplyOutcome.BadEdit, e.Advisor().Apply(e.Game, advice));
}
```
(Копии `.reg` при `BadEdit` могут быть созданы: `RegistryConfig.Apply` проверяет правки сам, после `Backup`; тест проверяет только исход.)

- [ ] **Step 2: Убедиться, что тесты падают** — Run: `dotnet test … --filter "FullyQualifiedName~AdvisorTests"`; Expected: новые FAIL/ошибка компиляции (`ConfigStore.BackupDir`), старые PASS.

- [ ] **Step 3: Реализовать реестровую ветку `ConfigStore` и правки `Advisor`**

- [ ] **Step 4: Прогнать все тесты** — Run: `dotnet build && dotnet test tests/metGameHUB.Tests`; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/metGameHUB/Advice tests/metGameHUB.Tests/Advice/AdvisorTests.cs
git commit -m "Advisor: read, apply and restore Unity PlayerPrefs through ConfigStore"
```

---

### Task 7: Документация и ручная проверка

**Files:**
- Modify: `CLAUDE.md` («Структура»: `ConfigStore`, `RegistryConfig` в `Advice/`; «Данные»: папка `registry\`; «Неочевидное»: один пункт про формат `registry`)
- Modify: `docs/backlog.md` (убрать пункт «Конфиги в реестре», в «Порядок крупных тем» отметить сделанным)
- Modify: `README.md`, если в нём перечислены форматы конфигов (`grep -n "INI" README.md`): добавить реестр Unity одной фразой.

- [ ] **Step 1: Записать в `CLAUDE.md`**

Пункт «Неочевидного» (суть, своими словами): формат `registry` — ключ `HKCU\Software\<Компания>\<Продукт>` в том же `ConfigPath`; подсказка модели из `<Игра>_Data\app.info`; допуск только под `HKCU\Software` глубиной ≥ 2 без `Microsoft`/`Classes`/`Policies`/`Wow6432Node`; выгрузка `имя=значение`, DWORD > 1 000 000 с конечным float печатается как float; пишутся только существующие значения с тем же типом, имя точное или без `_h<хэш>`, если единственное; копии `.orig.reg`/`.bak.reg` в `%AppData%\metGameHUB\registry\` через `reg export`/`reg import`; свежесть снимка — время записи ключа (`RegQueryInfoKey`); тесты реестра работают в `HKCU\Software\metGameHUB.Tests\<guid>`.

- [ ] **Step 2: Обновить `docs/backlog.md` и при необходимости `README.md`**

- [ ] **Step 3: Ручная проверка через терминал пользователя**

Собрать `dotnet build`, запустить приложение, открыть страницу Unity-игры из библиотеки, «Найти конфиг» → в строке конфига ключ `HKCU\Software\…`; «Подобрать» → таблица «Сейчас | Рекомендуется»; «Применить» при закрытой игре → `regedit` показывает новые значения, в `%AppData%\metGameHUB\registry\` два файла; «Вернуть исходный конфиг» → старые значения. Если Unity-игры в библиотеке нет — проверить на тестовом ключе, подставив его в `library.json` руками, и отметить это в отчёте.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/backlog.md README.md
git commit -m "Docs: Unity PlayerPrefs configs"
```
