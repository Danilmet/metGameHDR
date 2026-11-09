# Одна цель (ползунок) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** заменить пару «FPS + приоритет» одной целью — шагом ползунка `GoalStep`, FPS которого считается от частоты монитора.

**Architecture:** новый enum `GoalStep` и чистый класс `GoalTargets` (FPS, вид цели, режим для модели, подпись). Хранится только шаг (`GameOverride.Target`, `Settings.DefaultTarget`), FPS считается при показе и при запросе. UI: `Slider` 0–4 на странице игры и в настройках.

**Tech Stack:** .NET 8, WPF, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-09-single-goal-design.md`

## Global Constraints

- Шаги по порядку (индекс ползунка = `(int)GoalStep`): `MaxQuality, Pretty, Balanced, Smooth, MaxFps`.
- Подписи: «Макс. качество», «Красиво», «Баланс», «Плавно», «Макс. FPS». Режимы для модели: `max_quality`, `quality`, `balanced`, `smooth`, `max_fps`.
- FPS: MaxQuality 30 (AtLeast); Pretty `min(60, hz)`; Smooth `hz`; Balanced `Math.Clamp((int)Math.Round((pretty + smooth) / 20.0, MidpointRounding.AwayFromZero) * 10, pretty, smooth)`; MaxFps null (Unlimited). `hz <= 0` → 60.
- Строка: `"{Подпись} · цель ~{fps} FPS"` (Target), `"{Подпись} · не ниже {fps} FPS"` (AtLeast), `"{Подпись} · без ограничения"` (Unlimited); при значении по умолчанию + `" (по умолчанию)"`.
- Новые имена свойств: `GameOverride.Target` (`GoalStep?`), `Settings.DefaultTarget` (`GoalStep`, по умолчанию `Balanced`). Старые `Goal`, `DefaultFps`, `DefaultPriority` удаляются без переноса.
- `GoalStep` несёт `[JsonConverter(typeof(JsonStringEnumConverter))]`: `Library` и `Settings` сериализуют без общего конвертера.
- Тесты в `tests/.../Advice` — namespace `MetGameHUB.Tests`. Сторонних пакетов не добавлять. Комментарии только для неочевидного.

## Review Focus

1. Нестандартная частота (59, 75, 30 Гц): Balanced не выходит за пределы [Pretty, Smooth] — тест `For_OddRefreshRates_BalancedStaysBetween` в Task 1.
2. `library.json` старой версии с объектом `Goal`: файл не уходит в `.bad`, остальные переопределения целы — тест в Task 2.
3. `settings.json` старой версии с `DefaultFps`/`DefaultPriority`: загружается, `DefaultTarget` = `Balanced` — тест в Task 2.
4. Смена значения по умолчанию при открытой странице игры без своей цели: ползунок и строка обновляются (`NotifyDefaultsChanged` уведомляет `TargetIndex`, `TargetText`) — ручная проверка в Task 2.
5. Перетаскивание ползунка и «Сбросить» во время запроса к Claude недоступны (`IsEnabled="{Binding IsIdle}"`) — ручная проверка в Task 2.

---

### Task 1: `GoalStep` и `GoalTargets`

**Files:**
- Modify: `src/metGameHUB/Advice/Models.cs` (добавить enum рядом с `Priority`; `Priority`/`GraphicsGoal` пока не трогать)
- Create: `src/metGameHUB/Advice/GoalTargets.cs`
- Test: `tests/metGameHUB.Tests/Advice/GoalTargetsTests.cs`

**Interfaces:**
- Produces:
  - `[JsonConverter(typeof(JsonStringEnumConverter))] public enum GoalStep { MaxQuality, Pretty, Balanced, Smooth, MaxFps }`
  - `public enum GoalKind { AtLeast, Target, Unlimited }`
  - `public static class GoalTargets` с `(int? Fps, GoalKind Kind) For(GoalStep step, int hz)`, `string Mode(GoalStep step)`, `string Label(GoalStep step, int hz, bool isDefault)`

- [ ] **Step 1: Тесты**

```csharp
[Theory]
[InlineData(60, 60, 60, 60)]
[InlineData(144, 60, 100, 144)]
[InlineData(165, 60, 110, 165)]
[InlineData(240, 60, 150, 240)]
[InlineData(0, 60, 60, 60)]
public void For_TargetsFollowRefreshRate(int hz, int pretty, int balanced, int smooth)
{
    Assert.Equal((30, GoalKind.AtLeast), GoalTargets.For(GoalStep.MaxQuality, hz));
    Assert.Equal((pretty, GoalKind.Target), GoalTargets.For(GoalStep.Pretty, hz));
    Assert.Equal((balanced, GoalKind.Target), GoalTargets.For(GoalStep.Balanced, hz));
    Assert.Equal((smooth, GoalKind.Target), GoalTargets.For(GoalStep.Smooth, hz));
    Assert.Equal(((int?)null, GoalKind.Unlimited), GoalTargets.For(GoalStep.MaxFps, hz));
}

[Theory]
[InlineData(59, 59)]
[InlineData(75, 70)]
[InlineData(30, 30)]
public void For_OddRefreshRates_BalancedStaysBetween(int hz, int balanced) =>
    Assert.Equal(balanced, GoalTargets.For(GoalStep.Balanced, hz).Fps);

[Fact]
public void Label_PerKindAndDefaultSuffix()
{
    Assert.Equal("Баланс · цель ~110 FPS", GoalTargets.Label(GoalStep.Balanced, 165, false));
    Assert.Equal("Макс. качество · не ниже 30 FPS (по умолчанию)", GoalTargets.Label(GoalStep.MaxQuality, 165, true));
    Assert.Equal("Макс. FPS · без ограничения", GoalTargets.Label(GoalStep.MaxFps, 165, false));
}

[Fact]
public void Mode_MapsSteps() =>
    Assert.Equal(["max_quality", "quality", "balanced", "smooth", "max_fps"], Enum.GetValues<GoalStep>().Select(GoalTargets.Mode));
```

- [ ] **Step 2:** `dotnet test --filter GoalTargetsTests` — не компилируется (типов нет).
- [ ] **Step 3:** Добавить enum и `GoalTargets` по Global Constraints (switch-выражения).
- [ ] **Step 4:** `dotnet test --filter GoalTargetsTests` — все зелёные.
- [ ] **Step 5:** Коммит `Add GoalStep and GoalTargets`.

---

### Task 2: Переход на `GoalStep` (данные, запрос, UI, документы)

**Files:**
- Modify: `src/metGameHUB/Advice/Models.cs:5-8` — удалить `Priority` и `GraphicsGoal`
- Modify: `src/metGameHUB/Scan/Game.cs:20` — `Goal` → `public Advice.GoalStep? Target { get; set; }`
- Modify: `src/metGameHUB/Settings.cs:19-20` — `DefaultFps`/`DefaultPriority` → `public Advice.GoalStep DefaultTarget { get; set; } = Advice.GoalStep.Balanced;`
- Modify: `src/metGameHUB/Advice/ClaudeClient.cs:11,32,35,94` — `AdviceRequest.Goal` типа `GoalStep`; промпт; тело
- Modify: `src/metGameHUB/Advice/Advisor.cs:47,57` — `AskAsync(Game game, GoalStep goal, CancellationToken ct)`, `o.Target = goal`
- Modify: `src/metGameHUB/Ui/GameRow.cs:21,34,158-193,265,313`, `src/metGameHUB/Ui/MainWindow.xaml:207-229,417-425`, `src/metGameHUB/Ui/MainWindow.xaml.cs:53-54,89-104,185,384`
- Modify: `CLAUDE.md`, `README.md:10,53,70`, `docs/backlog.md` (удалить пункт «Одна цель вместо двух»)
- Test: `tests/metGameHUB.Tests/Advice/ClaudeClientTests.cs`, `Advice/AdvisorTests.cs`, `Scan/LibraryTests.cs`, `SettingsTests.cs`

**Interfaces:**
- Consumes: `GoalStep`, `GoalTargets.For/Mode/Label` из Task 1.
- Produces: `AdviceRequest(Game, HardwareInfo, GoalStep Goal, …)`; `Advisor.AskAsync(Game, GoalStep, CancellationToken)`; `GameRow(Game, Library, SteamStatus?, Action, FinderRoots, Func<GoalStep> defaultTarget, Func<int> hz, Func<bool> hasKey)`; `GameRow.Goal : GoalStep`, `TargetIndex : int` (get/set), `TargetText : string`, `HasOwnTarget : bool`, `ResetTarget()`.

- [ ] **Step 1: Тесты (меняют существующие и добавляют новые)**

```csharp
// ClaudeClientTests: Request() передаёт GoalStep.Balanced (hardware 144 Гц)
[Fact]
public void BuildAskBody_GoalHasModeAndFps()
{
    var user = UserJson(ClaudeClient.BuildAskBody(Request(), "m"));   // хелпер разбирает messages[0].content
    Assert.Equal("balanced", user.GetProperty("goal").GetProperty("mode").GetString());
    Assert.Equal(100, user.GetProperty("goal").GetProperty("fps").GetInt32());
    var max = UserJson(ClaudeClient.BuildAskBody(Request() with { Goal = GoalStep.MaxFps }, "m"));
    Assert.Equal(JsonValueKind.Null, max.GetProperty("goal").GetProperty("fps").ValueKind);
}

// AdvisorTests.AskAsync_StoresAdviceOnOverrideAndSaves: goal = GoalStep.Pretty; Assert.Equal(GoalStep.Pretty, e.Lib.For(e.Game).Target)
// остальные вызовы AskAsync: GoalStep.Balanced

// LibraryTests: в roundtrip Target = GoalStep.Smooth, Assert.Contains("\"Smooth\""); в Load_OldFileWithoutAdviceFields Assert.Null(o.Target)
[Fact] public void Load_OldGoalObject_IsDroppedNotBad()
{
    // JSON: { "Overrides": { "C:\\G": { "HdrEnabled": false, "Goal": { "Fps": 120, "Priority": "Responsive" }, "ConfigPath": "C:\\G\\a.ini" } } }
    var o = Library.Load(path).Overrides[@"C:\G"];
    Assert.False(o.HdrEnabled); Assert.Null(o.Target); Assert.Equal(@"C:\G\a.ini", o.ConfigPath);
    Assert.False(File.Exists(path + ".bad"));
}

// SettingsTests: roundtrip с DefaultTarget = GoalStep.Smooth, Assert.Contains("\"DefaultTarget\": \"Smooth\"")
[Fact] public void Load_OldDefaultFpsAndPriority_FallsBackToBalanced()
{
    // JSON: { "ClaudeModel": "m", "DefaultFps": 90, "DefaultPriority": "Quality" }
    var s = Settings.Load(path);
    Assert.Equal(GoalStep.Balanced, s.DefaultTarget); Assert.Equal("m", s.ClaudeModel);
    Assert.False(File.Exists(path + ".bad"));
}
```

- [ ] **Step 2:** `dotnet test` — не компилируется.
- [ ] **Step 3: Данные и запрос.** Удалить `Priority`/`GraphicsGoal`, заменить свойства в `Game.cs` и `Settings.cs`. `BuildAskBody`: `goal = new { mode = GoalTargets.Mode(r.Goal), fps = GoalTargets.For(r.Goal, r.Hardware.Hz).Fps }`. `AskPrompt`: во вводной «цель (кадры в секунду и приоритет: картинка, баланс или отзывчивость)» → «цель (режим и кадры в секунду)»; в правиле 2 убрать «Цель по кадрам — для типичных сцен, с запасом ~10%.»; добавить правило (номера дальше сдвигаются):

```text
2. goal.mode задаёт, чем жертвовать: max_quality — максимум картинки, кадры как получатся, но не ниже goal.fps; quality — стабильно держать goal.fps, весь остальной запас отдать картинке; balanced — держать goal.fps в типичных сценах, жертвовать самым дорогим при малой разнице в картинке; smooth — держать goal.fps с запасом и по минимальным кадрам (1% low), картинка во вторую очередь; max_fps — как можно больше кадров и минимальная задержка, картинка вторична (goal.fps = null). Цель по кадрам — для типичных сцен, с запасом ~10%.
```

  `Advisor.AskAsync` сохраняет `o.Target = goal`.
- [ ] **Step 4: `GameRow`.** Конструктор: `Func<GoalStep> defaultTarget, Func<int> hz` вместо `Func<GraphicsGoal> defaultGoal`. `Goal => Target ?? defaultTarget()`. `TargetIndex` set: `lib.For(Game).Target = (GoalStep)value; SaveSoon();` и уведомить `TargetIndex`, `TargetText`, `HasOwnTarget`. `ResetTarget()`: `Target = null; SaveSoon();` и те же уведомления. `TargetText => GoalTargets.Label(Goal, hz(), !HasOwnTarget)`. В списках `LoadAdvice` и `NotifyDefaultsChanged` заменить `GoalFps`/`GoalPriority`/`FpsHint` на `TargetIndex`, `TargetText`, `HasOwnTarget`.
- [ ] **Step 5: XAML и окно.** Страница игры: вместо `Grid` с `GoalFpsBox` и `ComboBox` приоритета —

```xml
<TextBlock Text="Качество" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}"/>
<Slider x:Name="GoalSlider" Width="220" Margin="8,0" Minimum="0" Maximum="4" TickFrequency="1" IsSnapToTickEnabled="True"
        TickPlacement="BottomRight" VerticalAlignment="Center" IsEnabled="{Binding IsIdle}" Value="{Binding TargetIndex}"/>
<TextBlock Text="Производительность" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}"/>
```

  и под строкой (новая строка `StackPanel` в Grid.Row="1" или ниже в той же колонке) `TextBlock Text="{Binding TargetText, Mode=OneWay}"` плюс `Button Content="Сбросить" Style="{StaticResource LinkButton}" Click="ResetTarget_Click" IsEnabled="{Binding IsIdle}" Visibility="{Binding HasOwnTarget, Converter={StaticResource BoolToVisibility}}"`. Фокус при открытии: `GoalSlider.Focus()`. Настройки: вместо `DefaultFpsBox` и `DefaultPriorityBox` — `DefaultTargetSlider` (те же свойства, без биндинга) и `DefaultTargetText`; в коде `Value = (int)settings.DefaultTarget`, `ValueChanged` → `settings.DefaultTarget = (GoalStep)(int)e.NewValue; SaveSettings(); DefaultTargetText.Text = GoalTargets.Label(settings.DefaultTarget, monitorHz, false); NotifyDefaultsChanged();`. `DefaultGoal()` удалить; `GameRow` получает `() => settings.DefaultTarget` и `() => monitorHz`.
- [ ] **Step 6:** `dotnet build` (0 предупреждений) и `dotnet test` — всё зелёное.
- [ ] **Step 7: Ручная проверка** (через терминал пользователя или после закрытия установленного хаба): ползунок на странице игры двигается, строка меняется, «Сбросить» появляется и возвращает «(по умолчанию)»; смена ползунка в настройках меняет строку игры без своей цели; во время «Подобрать» ползунок и «Сбросить» недоступны.
- [ ] **Step 8: Документы.** `CLAUDE.md`: в «Данные» — `settings.json` `DefaultTarget` вместо «FPS и приоритет по умолчанию», `library.json` `Target` вместо `Goal`; пункт «Приоритет по умолчанию и пустое поле FPS» заменить на «Цель игры — шаг ползунка `GoalStep` (null = по умолчанию из настроек); FPS шага считает `GoalTargets` от частоты монитора, модель получает `goal {mode, fps}`; старые `Goal`/`DefaultFps`/`DefaultPriority` при загрузке отбрасываются». `README.md` строки 10, 53, 70 — описать ползунок. `docs/backlog.md` — удалить пункт.
- [ ] **Step 9:** Коммит `Single graphics goal: slider instead of FPS + priority`.
