# Автосортировка, правила с папкой назначения, несколько папок, `#prefix` — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** пункты 1–4 из `TODO.md`: профиль настроек у каждой папки, правила с папкой назначения, сортировка нескольких папок за раз, `#prefix`/«Name», автосортировка в фоне с треем, уведомлениями и автозапуском.

**Architecture:** вся логика в `FileSorter.Core` (`net10.0`, без WPF): профили, планировщик сортировки (`OrganizeRequest` → `PlannedGroup`), правила, журнал копий, движок автосортировки, построитель превью. Движок автосортировки живёт на UI-потоке (как `MainViewModel`): события `FileSystemWatcher` и таймеры переводятся на него через `IUiDispatcher`, а тяжёлая работа уходит в `Task.Run` внутри `FileOrganizerService`, поэтому история и профили меняются только на UI-потоке. Трей, toast-уведомления, автозапуск и «один экземпляр» — в `FileSorter` (WPF) за интерфейсами `INotificationService` / `IAutostartService` или в `App`.

**Tech Stack:** C# / .NET 10, WPF, NUnit 4 (`NUnit` 4.3.2, `NUnit3TestAdapter` 5.0.0, `Microsoft.NET.Test.Sdk` 17.14.1 — уже есть в кеше NuGet), `System.Windows.Forms.NotifyIcon` (трей), `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (toast с кнопкой).

**Spec:** `docs/superpowers/specs/2026-10-18-auto-organize-rules-multifolder-design.md`

## Global Constraints

- `FileSorter.Core` — только `net10.0`, без WPF и Windows-only API (`CLAUDE.md`).
- Файлы пользователя никогда не удаляются; всё, что делает автосортировка, отменяемо.
- Тяжёлая работа — вне UI-потока; EXIF — только лениво.
- Совместимость с существующими `settings.json` и `history.json` (миграция).
- Задержки автосортировки по умолчанию: debounce **30 с**, окно стабильности файла **30 с**, уведомления — не чаще одного раза в **30 с** на папку.
- Пропускаемые расширения незаконченных загрузок: `.crdownload`, `.part`, `.partial`, `.tmp`, `.download`, `.opdownload`.
- Лимиты истории: **50** ручных и **200** автоматических операций, считаются отдельно.
- Сборка/тесты из WSL: `cmd.exe /c "cd /d C:\Projects\CSharp\FileSorter && dotnet test FileSorter.sln -nologo -v q"`; ожидается 0 warnings, 0 errors, все тесты зелёные.
- Тесты не трогают реальный `%LocalAppData%\FileSorter`: каталог данных подменяется (Task 1).
- Код, комментарии и строки UI — на английском, как сейчас (локализация — пункт 9 бэклога).

## Review Focus

1. **Firefox-загрузка:** Firefox создаёт пустой `report.pdf` и рядом `report.pdf.part`; заглушка старше 30 с, если загрузка долгая. Ожидание: файл, рядом с которым есть `<имя><расширение незаконченной загрузки>`, не трогается. Тест — Task 9 (`Candidates_SkipFileWithPartialSibling`).
2. **Два экземпляра программы** (автозапуск + ручной запуск) одновременно сортируют `Downloads`. Ожидание: второй запуск активирует окно первого и завершается. Проверка — Task 11 (ручная, шаг «single instance»).
3. **Папка автосортировки исчезла** (отключили флешку, переименовали). Ожидание: запуск тихо пропускается, программа не падает, после возвращения папки всё работает. Тест — Task 9 (`RunNow_MissingFolder_NoOperationNoThrow`).
4. **Недоступная папка из правила** (`Q:\Video` без диска Q:). Ожидание: файлы этого правила считаются неудачными, остальные группы раскладываются, операция в истории. Тест — Task 4 (`Organize_RuleToUnavailableDrive_CountsFailedAndContinues`).
5. **Один путь в разном написании** (`C:\Users\a\Downloads` и `c:\users\a\downloads\`). Ожидание: один профиль, одна запись в списке. Тест — Task 2 (`FindProfile_IgnoresCaseAndTrailingSeparator`).

---

### Task 1: Тестовый проект и `#prefix` / способ «Name»

**Files:**
- Create: `FileSorter.Core.Tests/FileSorter.Core.Tests.csproj`, `FileSorter.Core.Tests/TempDataDirTest.cs`, `FileSorter.Core.Tests/NamePrefixTests.cs`, `FileSorter.Core.Tests/GroupingTests.cs`
- Create: `FileSorter.Core/Services/NamePrefix.cs`
- Modify: `FileSorter.Core/FileSorter.Core.csproj` (`<InternalsVisibleTo Include="FileSorter.Core.Tests" />`), `FileSorter.Core/Services/JsonFileStore.cs` (`DataDir`), `FileSorter.Core/Models/OrganizeMethod.cs`, `FileSorter.Core/Services/FileOrganizerService.cs` (`ExpandTemplate`, `GroupFiles`, словари групп, `GeneratePreview`), `FileSorter.Core/ViewModels/MainViewModel.cs` (`TypeCountText`), `FileSorter/Views/MainWindow.xaml` (кнопка способа), `FileSorter.sln`

**Interfaces:**
- Produces: `internal static string JsonFileStore.DataDir { get; set; }` (по умолчанию `%LocalAppData%/FileSorter`); абстрактный класс тестов `TempDataDirTest` с `protected string TempDir` (создаётся в `[SetUp]`, подменяет `DataDir`, в `[TearDown]` восстанавливает и удаляет); `public static string NamePrefix.Get(string fileName)`; `OrganizeMethod.ByName` (добавляется **в конец** enum).

- [ ] **Step 1: Создать тестовый проект** `net10.0`, `IsPackable=false`, NUnit 4.3.2 / NUnit3TestAdapter 5.0.0 / Microsoft.NET.Test.Sdk 17.14.1, ссылка на `FileSorter.Core`; `dotnet sln add`. `TempDataDirTest` — как описано в Interfaces.

- [ ] **Step 2: Написать падающие тесты**

```csharp
[TestCase("hello-world-1.jpg", "hello-world")]
[TestCase("IMG_2034.jpg", "IMG")]
[TestCase("photo (3).png", "photo")]
[TestCase("IMG2034.jpg", "IMG")]
[TestCase("file2.txt", "file")]
[TestCase("report_v2.docx", "report_v")]
[TestCase("2034.jpg", "#")]
[TestCase("README", "README")]
public void Get_StripsTrailingNumbersAndSeparators(string name, string expected) =>
    Assert.That(NamePrefix.Get(name), Is.EqualTo(expected));
```

`GroupingTests.ByName_GroupsCaseInsensitively_KeepsFirstSeenName`: файлы `Photo-1.jpg`, `photo-2.jpg` → `GeneratePreview(..., OrganizeMethod.ByName, ...)` даёт одну папку `Photo` с 2 файлами. `GroupingTests.Template_PrefixToken`: шаблон `#prefix/#ext` для `hello-world-1.jpg` → путь `hello-world/JPG`.

- [ ] **Step 3: Запустить — FAIL** (`NamePrefix` не существует).

- [ ] **Step 4: Реализовать.** `NamePrefix.Get`: имя без расширения, затем в цикле отрезать с конца одно из: `\(\d+\)`, `\d+`, `[\s._\-]+`, пока что-то отрезается; пустой результат → `"#"`. `#prefix` в `ExpandTemplate`; `ByName` в `GroupFiles`. Все словари групп и поиск узла в `GeneratePreview` — `StringComparer.OrdinalIgnoreCase` (Windows-пути регистронезависимы, первый ключ сохраняется). `TypeCountText`: `ByName => "{n} Names"`. Кнопка «Name» в XAML после существующих кнопок способов, по образцу соседних.

- [ ] **Step 5: Запустить тесты и сборку — PASS, 0 warnings.**

- [ ] **Step 6: Commit** `feat: add #prefix token and Name method, Core test project`

---

### Task 2: Профиль папки, настройки v2 и миграция

**Files:**
- Create: `FileSorter.Core/Models/FolderProfile.cs`, `FileSorter.Core/Models/RoutingRule.cs`, `FileSorter.Core/Models/AutoOrganizeSettings.cs`, `FileSorter.Core/Services/PathKey.cs`, `FileSorter.Core.Tests/SettingsMigrationTests.cs`
- Modify: `FileSorter.Core/Services/SettingsService.cs`

**Interfaces:**
- Produces:
  - `enum UnmatchedFilesMode { OrganizeNormally, LeaveInPlace }`, `enum ScheduleUnit { Minutes, Hours, Days }`
  - `class AutoOrganizeSettings { bool Enabled; bool OnNewFiles = true; bool ScheduleEnabled; int ScheduleInterval = 1; ScheduleUnit ScheduleUnit = Hours; bool IsPaused; }`
  - `class RoutingRule { bool IsEnabled = true; List<FilterRule> Conditions = []; bool MatchAll = true; string Destination = ""; }`
  - `class FolderProfile { string FullPath; string Name; int FileCount; OrganizeMethod Method = ByType; string Template = ""; DateGranularity DateGranularity = Month; bool ApplyFilters = true; List<string> DisabledCategories = []; string? DestinationFolder; bool CopyMode; bool IncludeSubfolders = true; bool SkipDuplicates; List<RoutingRule> Rules = []; UnmatchedFilesMode Unmatched = OrganizeNormally; AutoOrganizeSettings Auto = new(); static FolderProfile CreateDefault(string fullPath); }` (`Name` = имя последнего сегмента пути или сам путь для корня диска)
  - `AppSettings` (v2): `int Version = 2; List<FolderProfile> Folders = []; bool IsDarkTheme = true; FileViewMode SelectedViewMode = Tree; bool StartWithWindows; bool PauseAll; bool TrayHintShown;` + `FolderProfile? FindProfile(string path)`
  - `static class PathKey { string Normalize(string path); bool Equal(string a, string b); bool IsUnder(string path, string folder); }` — `GetFullPath` + `TrimEndingDirectorySeparator`, `OrdinalIgnoreCase`; `FileOrganizerService.PathsEqual/IsUnderFolder` переходят на него.
  - `SettingsService.Load()` возвращает v2 всегда; `Save(AppSettings)` без изменений.

- [ ] **Step 1: Падающие тесты** (наследуют `TempDataDirTest`, пишут `settings.json` в `TempDir`):
  - `Load_LegacyFile_CopiesGlobalSettingsIntoEveryRecentFolder`: старый JSON с `RecentFolders` (2 шт.), `CopyMode: true`, `SelectedMethod: "ByDate"`, `DestinationSubfolderPattern: "#year"`, `SkipDuplicates: true`, `IsDarkTheme: false` → `Folders.Count == 2`, у обоих `CopyMode`, `Method == ByDate`, `Template == "#year"`, `SkipDuplicates`; `IsDarkTheme == false`; `Version == 2`.
  - `Load_LegacyNumericEnums_Accepted`: `"SelectedMethod": 2` → `ByDate`.
  - `Load_V2_RoundTrips`: профиль с правилом и `Auto.Enabled` сохраняется и читается обратно.
  - `Load_InvalidEnumInProfile_FallsBackToDefault`: `"Method": 99` → `ByType`.
  - `FindProfile_IgnoresCaseAndTrailingSeparator`: профиль `C:\Users\a\Downloads`, поиск `c:\users\a\downloads\` → найден.
  - `Load_NoFile_ReturnsDefaults`.

- [ ] **Step 2: Запустить — FAIL.**

- [ ] **Step 3: Реализовать.** Миграция в `SettingsService.Load`: прочитать `AppSettings`; если `Version < 2` — перечитать тот же файл как `internal sealed class LegacyAppSettings` (поля старого `AppSettings`) и построить профили; `ApplyRules`/`RulesAndMode` отбрасываются. Нормализация недопустимых enum (`Enum.IsDefined`) — здесь, для каждого профиля (переносится из `MainViewModel.ValidOrDefault`). Дубликаты путей при миграции схлопываются через `PathKey`.

- [ ] **Step 4: PASS.**

- [ ] **Step 5: Commit** `feat: per-folder profiles in settings v2 with migration`

---

### Task 3: `MainViewModel` на профилях, список папок без вытеснения

**Files:**
- Create: `FileSorter.Core/ViewModels/FolderViewModel.cs`, `FileSorter.Core.Tests/Fakes.cs`, `FileSorter.Core.Tests/MainViewModelProfileTests.cs`
- Modify: `FileSorter.Core/ViewModels/MainViewModel.cs`, `FileSorter/Views/MainWindow.xaml` (секция Folders)
- Delete: `FileSorter.Core/Models/RecentFolder.cs`

**Interfaces:**
- Consumes: `FolderProfile`, `AppSettings.FindProfile`, `PathKey` (Task 2).
- Produces:
  - `enum AutoStatus { Off, On, Paused }`
  - `class FolderViewModel : ObservableObject { FolderProfile Profile; string Name; string FullPath; int FileCount; bool IsChecked; bool IsSelected; AutoStatus AutoStatus; void RefreshStatus(bool pauseAll); }`
  - `MainViewModel`: `ObservableCollection<FolderViewModel> Folders`; `FolderViewModel? SelectedFolder`; `ICommand SelectFolderCommand(FolderViewModel)`; `ICommand RemoveFolderCommand(FolderViewModel)`; приватное `FolderProfile _profile` — единственное хранилище per-folder свойств.
  - Fakes: `InlineDispatcher : IUiDispatcher` (выполняет сразу), `FakeDialogs` (`Queue<string?> Folders`), `NullThumbnails`, `NullTheme`.

- [ ] **Step 1: Падающие тесты** (`TempDataDirTest`, VM через fakes):
  - `SelectFolder_LoadsItsProfileIntoProperties`: две папки с разными `CopyMode`/`Method` → выбор второй даёт её значения в `CopyMode`, `SelectedMethod`.
  - `ChangingProperty_WritesToSelectedProfileOnly`: выбрать A, `CopyMode = true`, выбрать B → `B.CopyMode == false`, `A.Profile.CopyMode == true`.
  - `CategoryToggle_PersistsInProfile`: снять `Images` → `Profile.DisabledCategories` содержит `"Images"`; после `SaveSettings()` и нового VM категория снова снята при выборе папки.
  - `AddFolder_ManyFolders_NoneEvicted`: 15 папок через `AddFolderCommand` → `Folders.Count == 15`.
  - `AddFolder_ExistingPathDifferentCase_SelectsExisting`.
  - `NewFolder_GetsBuiltInDefaults`: после изменения `CopyMode` у A добавить новую B → `B.CopyMode == false`.

- [ ] **Step 2: FAIL.**

- [ ] **Step 3: Реализовать.** Per-folder свойства (`SelectedMethod`, `DestinationSubfolderPattern`, `SelectedDateGranularity`, `ApplyFilters`, `DestinationFolder`, `CopyMode`/`MoveMode`, `IncludeSubfolders`, `SkipDuplicates`) читают/пишут `_profile`; без выбранной папки `_profile` = `FolderProfile.CreateDefault("")`, не сохраняется. Смена папки: присвоить `_profile`, выставить `IsSelected` категорий по `DisabledCategories`, поднять `PropertyChanged` для всех per-folder свойств, `RefreshFilesAsync()`. `SourceFolder` остаётся только для чтения (= `SelectedFolder?.FullPath ?? ""`). `SaveSettings`/`LoadSettings` — через `AppSettings.Folders`; лимит 10 и `AddCurrentToRecent` удаляются. XAML: блоки «Current»/«Recent» заменяются одним списком `Folders` (выбранная строка подсвечена, кнопка удаления как сейчас у Recent). Порядок: новая папка — в начало.

- [ ] **Step 4: PASS; сборка; ручная проверка: выбрать две папки, поменять настройки, перезапустить — настройки на месте.**

- [ ] **Step 5: Commit** `feat: per-folder settings in UI, managed folder list`

---

### Task 4: `OrganizeRequest`, планировщик и правила с папкой назначения (Core)

**Files:**
- Create: `FileSorter.Core/Services/OrganizeRequest.cs`, `FileSorter.Core.Tests/PlannerTests.cs`, `FileSorter.Core.Tests/OrganizeRulesTests.cs`
- Modify: `FileSorter.Core/Models/RoutingRule.cs` (snapshot), `FileSorter.Core/Models/FilterRule.cs`, `FileSorter.Core/Services/FileOrganizerService.cs`, `FileSorter.Core/ViewModels/MainViewModel.cs` (вызовы), `FileSorter.Core/ViewModels/FilterRuleViewModel.cs`

**Interfaces:**
- Consumes: `FolderProfile`, `RoutingRule`, `UnmatchedFilesMode` (Task 2).
- Produces:
  - `sealed record RoutingRuleSnapshot(IReadOnlyList<FilterRuleSnapshot> Conditions, bool MatchAll, string Destination) { bool Matches(FileItem f); string ResolveFolder(string targetFolder); }` — `RoutingRule.Snapshot()` возвращает `null`, если правило выключено или `Destination` пустой; ноль условий = подходит любой файл; абсолютный путь (`Path.IsPathFullyQualified`) берётся как есть, иначе `Path.Combine(target, dest)`.
  - `sealed record OrganizeRequest(string SourceFolder, string? DestinationFolder, OrganizeMethod Method, string? Template, string DateFormat, IReadOnlyList<FileCategory> Categories, IReadOnlySet<string>? AllowedCategories, IReadOnlyList<RoutingRuleSnapshot> Rules, UnmatchedFilesMode Unmatched, bool CopyMode, bool SkipDuplicates) { string TargetFolder; static OrganizeRequest FromProfile(FolderProfile p, IReadOnlyList<FileCategory> categories); }` — `AllowedCategories == null`, если `ApplyFilters == false`; `FromProfile` вызывается на UI-потоке и полностью копирует данные.
  - `sealed record PlannedGroup(string FolderPath, IReadOnlyList<string> DisplaySegments, IReadOnlyList<FileItem> Files)` — `FolderPath` абсолютный; для пути вне целевой папки `DisplaySegments` = `[полный путь]`.
  - `FileOrganizerService`: `static IReadOnlyList<PlannedGroup> Plan(IReadOnlyList<FileItem> files, OrganizeRequest request)`; `ObservableCollection<PreviewItem> GeneratePreview(IReadOnlyList<FileItem> files, OrganizeRequest request)`; `Task<OrganizeOperation> OrganizeAsync(IReadOnlyList<FileItem> files, OrganizeRequest request, IProgress<int>? progress = null)` (параметр запуска добавит Task 6). Поле `_categories` и `SetCategories` удаляются.

- [ ] **Step 1: Падающие тесты**
  - `Plan_FirstMatchingRuleWins`: правила `[ext is mp4 → Movies, name contains trip → Trips]`, файл `trip.mp4` → группа `<target>\Movies`.
  - `Plan_RuleWithFullPath_UsesItAsIs`: `Destination = TempDir\Video` → `FolderPath` = он, `DisplaySegments.Count == 1`.
  - `Plan_Unmatched_LeaveInPlace_ExcludesFile`, `Plan_Unmatched_OrganizeNormally_UsesMethod`.
  - `Plan_DisabledRuleOrEmptyDestination_Ignored`.
  - `Plan_ApplyFiltersOff_IncludesAllCategories` (сейчас категории фильтруются всегда — это исправляется).
  - `Plan_CategoryFilterAppliedBeforeRules`.
  - `Organize_RuleFiles_UndoRestoresThem` (реальные файлы в `TempDir`).
  - `Organize_RuleToUnavailableDrive_CountsFailedAndContinues`: правило на `Q:\NoSuchDrive\X` (взять букву, которой нет в `DriveInfo.GetDrives()`) + обычная группа → `FailedFiles == 1`, обычный файл перенесён.

- [ ] **Step 2: FAIL.**

- [ ] **Step 3: Реализовать.** `Plan`: фильтр категорий → для каждого файла первое подходящее правило → иначе по `Unmatched` либо существующая группировка (`GroupFiles`, теперь статическая и принимающая `request`) → группы по `FolderPath` (`OrdinalIgnoreCase`). `OrganizeAsync` обходит `PlannedGroup` вместо словаря; логика дубликатов/«уже на месте»/`finally` без изменений. В `MainViewModel` `CaptureFilterState`/`FilterFiles`/`FilterState` удаляются — превью и сортировка строят `OrganizeRequest.FromProfile(_profile, categories)`. В `FilterRuleViewModel` удаляются `Matches`, `MatchString/Date/Size`, `ToModel` (матчинг — только `FilterRuleSnapshot`). Временно правила в UI не редактируются (Task 5).

- [ ] **Step 4: PASS; harness-сценарии из прошлых сессий (`<scratchpad>/harness`) — зелёные (с бэкапом `%LocalAppData%\FileSorter`).**

- [ ] **Step 5: Commit** `feat: organize planner with destination rules`

---

### Task 5: Редактор правил в UI

**Files:**
- Create: `FileSorter.Core/ViewModels/RoutingRuleViewModel.cs`, `FileSorter.Core.Tests/RulesEditorTests.cs`
- Modify: `FileSorter.Core/ViewModels/FilterRuleViewModel.cs`, `FileSorter.Core/ViewModels/MainViewModel.cs`, `FileSorter/Views/MainWindow.xaml` (секция RULES и чекбокс «Apply Rules» в центральной панели)

**Interfaces:**
- Consumes: `RoutingRule`, `FilterRule` (Task 2), `OrganizeRequest` (Task 4).
- Produces:
  - `FilterRuleViewModel(FilterRule model)` — свойства пишут в модель.
  - `RoutingRuleViewModel(RoutingRule model) { bool IsEnabled; bool MatchAll; string Destination; ObservableCollection<FilterRuleViewModel> Conditions; ICommand AddConditionCommand; event Action? Changed; }`
  - `MainViewModel`: `ObservableCollection<RoutingRuleViewModel> Rules` (пересобирается при смене папки из `_profile.Rules`); `AddRuleCommand`, `RemoveRuleCommand(RoutingRuleViewModel)`, `MoveRuleUpCommand`, `MoveRuleDownCommand`, `BrowseRuleDestinationCommand(RoutingRuleViewModel)` (через `IDialogService.PickFolder`); `UnmatchedFilesMode Unmatched`. Удаляются `ApplyRules`, `RulesAndMode`, `RulesOrMode`; `RulesActiveText` остаётся и равно `"RULES ON"`, когда у профиля есть хоть одно включённое правило.

- [ ] **Step 1: Падающие тесты**
  - `AddRule_AppearsInProfile`, `MoveRuleUp_ReordersProfileRules`, `RemoveRule_RemovesFromProfile`.
  - `EditingCondition_RefreshesPreview`: после изменения `Value` условия превью содержит папку правила.
  - `SwitchFolder_RebuildsRulesFromItsProfile`.

- [ ] **Step 2: FAIL. Step 3: Реализовать.** Вёрстка (решение исполнителя из спеки): в левой панели карточка на правило — чекбокс вкл., поле «Destination» + кнопка «…», переключатель All/Any, список условий (поле/оператор/значение/удалить, как сейчас), «+ condition», ↑/↓/✕; под списком ComboBox «Other files: Organize normally / Leave in place».

- [ ] **Step 4: PASS; ручная проверка: правило `ext is mp4 → D:\Video` видно в превью отдельным корневым узлом, разложение и Undo работают.**

- [ ] **Step 5: Commit** `feat: rules editor with destination folders`

---

### Task 6: История — запуски, «авто», раздельные лимиты

**Files:**
- Modify: `FileSorter.Core/Models/OrganizeOperation.cs`, `FileSorter.Core/Services/HistoryService.cs`, `FileSorter.Core/Services/FileOrganizerService.cs`, `FileSorter.Core/ViewModels/MainViewModel.cs`
- Create: `FileSorter.Core.Tests/HistoryRunTests.cs`

**Interfaces:**
- Produces:
  - `OrganizeOperation`: `Guid RunId`, `string SourceFolder = ""`, `bool IsAuto`. `Guid.Empty` (старые записи) — отдельный запуск из одной операции.
  - `readonly record struct RunContext(Guid RunId, bool IsAuto) { static RunContext NewManual(); static RunContext NewAuto(); }`
  - `OrganizeAsync(IReadOnlyList<FileItem> files, OrganizeRequest request, RunContext run, IProgress<int>? progress = null)`; `Description` = `"{folderName}: Moved N files by X"`, для авто с префиксом `"Auto · "`.
  - `HistoryService.MaxManualOperations = 50`, `MaxAutoOperations = 200` (`MaxOperations` удаляется).
  - `FileOrganizerService.LastUndoableRun : IReadOnlyList<OrganizeOperation>` (операции последнего запуска, которые `CanUndoOperation`; пусто — нечего отменять); `LastUndoableOperation` удаляется.
  - `MainViewModel`: `UndoCommand` отменяет `LastUndoableRun` в обратном порядке; событие `public event Action<IReadOnlyList<OrganizeOperation>>? OperationsUndone` (нужно Task 8/10).

- [ ] **Step 1: Падающие тесты**
  - `Trim_KeepsLimitsSeparately`: 60 ручных + 210 авто → 50 + 200, оставлены самые новые.
  - `LastUndoableRun_ReturnsAllOperationsOfLatestRun`: два запуска (второй из двух операций разных папок) → 2 операции второго.
  - `LastUndoableRun_LegacyEmptyRunId_IsSingleOperation`.
  - `Undo_LatestRunIsAuto_UndoesAuto`.

- [ ] **Step 2: FAIL. Step 3: Реализовать. Step 4: PASS.**

- [ ] **Step 5: Commit** `feat: run-based undo and separate history limits`

---

### Task 7: Несколько папок за раз

**Files:**
- Create: `FileSorter.Core/Services/PreviewBuilder.cs`, `FileSorter.Core.Tests/MultiFolderTests.cs`
- Modify: `FileSorter.Core/ViewModels/MainViewModel.cs` (статистика и `BuildViewFileGroups` переезжают в `PreviewBuilder`), `FileSorter/Views/MainWindow.xaml` (чекбоксы у папок, подпись кнопки)

**Interfaces:**
- Consumes: `OrganizeRequest`, `Plan`, `GeneratePreview` (Task 4), `RunContext`, `LastUndoableRun` (Task 6), `FolderViewModel.IsChecked` (Task 3).
- Produces:
  - `sealed record PreviewSource(string FolderName, OrganizeRequest Request, IReadOnlyList<FileItem> Files)`
  - `sealed record PreviewResult(IReadOnlyList<PreviewItem> Roots, IReadOnlyList<CategoryStats> Stats, IReadOnlyList<ViewFileGroup> ViewGroups, int FileCount)`
  - `static PreviewResult PreviewBuilder.Build(IReadOnlyList<PreviewSource> sources, IReadOnlyList<RgbColor> colors, Func<ViewFileItem, object?> thumbnailProvider)` — один источник: как сейчас (корень `Destination → X`, особая статистика для ByType без шаблона); несколько: корень на папку (`{FolderName} → {destination}`), статистика по именам групп верхнего уровня, сложенным по всем папкам; ключ `ViewFileGroup` = `"{FolderName}/{group}"`.
  - `MainViewModel`: `IReadOnlyList<FolderViewModel> OrganizeTargets` (отмеченные, иначе выбранная); `string OrganizeButtonText` (`"ORGANIZE"` / `"ORGANIZE {n} FOLDERS"`); сканы хранятся по папке `Dictionary<string, List<FileItem>>` (ключ `PathKey.Normalize`).

- [ ] **Step 1: Падающие тесты**
  - `Build_TwoSources_OneRootPerFolder_StatsSummed`.
  - `Organize_TwoCheckedFolders_EachWithOwnProfile_OneRunTwoOperations`: A — ByType, B — ByExtension (реальные файлы) → две операции с одним `RunId`, файлы разложены по своим профилям.
  - `Organize_OneFolderFails_OthersStillProcessed`: папка A удалена после сканирования → B разложена, в статусе есть ошибка A.
  - `Undo_AfterMultiFolderRun_RestoresBoth`.

- [ ] **Step 2: FAIL. Step 3: Реализовать.** Сортировка последовательно по `OrganizeTargets`, один `RunContext.NewManual()` на клик, ошибка папки ловится и попадает в итоговый статус (`"Downloads: 12 moved; Desktop: error …"`). Отметка/снятие папки запускает её сканирование или убирает её из превью.

- [ ] **Step 4: PASS; ручная проверка двух отмеченных папок.**

- [ ] **Step 5: Commit** `feat: organize multiple folders at once`

---

### Task 8: Журнал скопированных файлов

**Files:**
- Create: `FileSorter.Core/Services/CopyJournal.cs`, `FileSorter.Core.Tests/CopyJournalTests.cs`
- Modify: `FileSorter.Core/Services/FileOrganizerService.cs`, `FileSorter.Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Produces: `class CopyJournal` (файл `copy-journal.json` через `JsonFileStore`, сохранение в фоне под `SemaphoreSlim`, как `HistoryService`):
  - `static Task<CopyJournal> LoadAsync()`; `Task SaveAsync()`
  - `bool Contains(string sourceFolder, FileItem file)` — совпадают путь, размер и `LastWriteTimeUtc`
  - `void Add(string sourceFolder, string path, long size, DateTime lastWriteTimeUtc)`; `void Remove(string sourceFolder, string path)`
  - `void Prune(string sourceFolder, IEnumerable<string> existingRootFiles)` — удаляет записи о файлах, которых нет среди переданных
- `FileOrganizerService(CopyJournal journal)`: в режиме копирования после успешной копии и после пропуска дубликата — `Add` (исходный файл); при отмене копии — `Remove`; сохранение журнала вместе с историей.

- [ ] **Step 1: Падающие тесты**
  - `ManualCopy_AddsSourceToJournal`, `CopySkippedDuplicate_AddsToJournal`.
  - `Contains_ChangedFile_False` (изменили содержимое/mtime).
  - `UndoCopy_RemovesFromJournal`.
  - `Prune_RemovesMissingFiles`.
  - `Journal_SurvivesReload`.

- [ ] **Step 2: FAIL. Step 3: Реализовать. Step 4: PASS.**

- [ ] **Step 5: Commit** `feat: copy journal for repeated copy runs`

---

### Task 9: Движок автосортировки (Core)

**Files:**
- Create: `FileSorter.Core/Services/AutoOrganizeEngine.cs`, `FileSorter.Core/Services/AutoOrganizeCandidates.cs`, `FileSorter.Core/Services/FolderGate.cs`, `FileSorter.Core/Services/ProfileConflicts.cs`, `FileSorter.Core/Services/NotificationThrottle.cs`, `FileSorter.Core.Tests/AutoOrganizeCandidatesTests.cs`, `FileSorter.Core.Tests/AutoOrganizeEngineTests.cs`, `FileSorter.Core.Tests/ProfileConflictsTests.cs`, `FileSorter.Core.Tests/NotificationThrottleTests.cs`

**Interfaces:**
- Consumes: `FolderProfile`, `AutoOrganizeSettings` (Task 2), `OrganizeRequest` (Task 4), `RunContext` (Task 6), `CopyJournal` (Task 8), `FileOrganizerService.ScanFolderAsync`/`OrganizeAsync`.
- Produces:
  - `sealed record AutoOrganizeOptions(TimeSpan Debounce, TimeSpan Stability, TimeSpan NotificationInterval) { static AutoOrganizeOptions Default => (30 s, 30 s, 30 s); }`
  - `static class AutoOrganizeCandidates { static readonly string[] PartialExtensions; static CandidateResult Select(IReadOnlyList<FileItem> rootFiles, DateTime nowUtc, TimeSpan stability, Func<FileItem, bool> isProcessed, Func<string, bool> isLocked); }`, `sealed record CandidateResult(IReadOnlyList<FileItem> Ready, bool HasPending)` — `HasPending` = были нестабильные/заблокированные/с незаконченной загрузкой рядом.
  - `class FolderGate { bool TryEnter(string folder); void Exit(string folder); bool IsBusy(string folder); event Action<string>? Changed; }` — общий для ручных и авто операций.
  - `class AutoOrganizeEngine(FileOrganizerService organizer, CopyJournal journal, FolderGate gate, IUiDispatcher dispatcher, AutoOrganizeOptions options, Func<AutoOrganizeSettings, TimeSpan>? scheduleInterval = null)` — по умолчанию интервал = `ScheduleInterval` × `ScheduleUnit`; тесты подставляют короткий:
    - `void Configure(IReadOnlyList<FolderProfile> profiles, bool pauseAll)` — пересоздаёт watcher'ы/таймеры для профилей с `Auto.Enabled && !Auto.IsPaused && !pauseAll`; при первом вызове (и не на паузе) — одна проверка каждой такой папки.
    - `void NotifyChanged(string folder)` — перезапускает debounce (watcher вызывает его через dispatcher).
    - `Task<OrganizeOperation?> RunNowAsync(string folder)`
    - `event Action<string, OrganizeOperation>? RunCompleted` — только если что-то перемещено/скопировано.
    - `IDisposable`.
  - `static IReadOnlyList<string> ProfileConflicts.Find(FolderProfile profile, IReadOnlyList<FolderProfile> all)` — тексты предупреждений: папка назначения (или полный путь правила) равна корню другой авто-папки или лежит внутри него; корни двух авто-папок вложены друг в друга.
  - `class NotificationThrottle(TimeSpan interval) { void Add(string folder, OrganizeOperation op, DateTime nowUtc, Action<string, IReadOnlyList<OrganizeOperation>> flush); void Tick(DateTime nowUtc, Action<string, IReadOnlyList<OrganizeOperation>> flush); }` — первый запуск папки показывается сразу, следующие в пределах интервала копятся и показываются одним уведомлением.

- [ ] **Step 1: Падающие тесты кандидатов** (чистые, без таймеров):
  - `Candidates_SkipsPartialExtensions` (все 6 расширений).
  - `Candidates_SkipFileWithPartialSibling`: `report.pdf` + `report.pdf.part` → `report.pdf` не готов, `HasPending`.
  - `Candidates_ModifiedWithinStability_Pending`, `Candidates_StableFile_Ready`.
  - `Candidates_Locked_Pending`, `Candidates_InJournal_Skipped`.

- [ ] **Step 2: Падающие тесты движка** (реальные папки в `TempDir`, `AutoOrganizeOptions(200 ms, 0, 200 ms)`, `InlineDispatcher`, ожидание с таймаутом 5 с):
  - `NotifyChanged_Debounced_SingleRun`: 5 вызовов подряд → один `RunCompleted`.
  - `Run_OnlyRootFiles_SubfoldersUntouched`.
  - `Run_IsAuto_OperationInHistory`.
  - `Run_GateBusy_Deferred`: `gate.TryEnter(folder)` заранее → запуска нет; после `Exit` и `NotifyChanged` → есть.
  - `Run_CopyMode_SecondRunCopiesNothing`.
  - `Run_FilesItPlacedInWatchedRoot_Ignored`: правило папки A ведёт в корень авто-папки B → движок не разбирает в B файлы, которые сам туда положил.
  - `Configure_PausedOrPauseAll_NoStartupRun`; `Configure_Enabled_StartupRunOnce`.
  - `Schedule_RunsEveryInterval`: `scheduleInterval = _ => 300 ms`, `OnNewFiles = false` → за 1 с не меньше двух запусков (файлы подкладываются между ними).
  - `RunNow_MissingFolder_NoOperationNoThrow`.
- Тесты конфликтов и throttle — по описанию в Interfaces.

- [ ] **Step 3: FAIL.**

- [ ] **Step 4: Реализовать.** Запуск: `gate.TryEnter` (занято → повтор через `Debounce`) → `ScanFolderAsync(folder, false, …)` → `journal.Prune` → `AutoOrganizeCandidates.Select` (`isLocked` — попытка `File.Open(path, Open, Read, FileShare.None)`) → `OrganizeAsync(ready, OrganizeRequest.FromProfile(...), RunContext.NewAuto())` → `gate.Exit`. `HasPending` → повтор через `Stability`. Watcher: `Created`/`Renamed`/`Changed` на корне (`IncludeSubdirectories = false`), `Error` → пересоздать watcher при следующем `Configure`, без исключений наружу. Всё состояние движка трогается только на UI-потоке. Положенные движком в отслеживаемые корни файлы запоминаются (путь+размер+mtime) и пропускаются, пока не изменятся.

- [ ] **Step 5: PASS.**

- [ ] **Step 6: Commit** `feat: auto-organize engine`

---

### Task 10: Автосортировка в `MainViewModel` и UI

**Files:**
- Create: `FileSorter.Core/Abstractions/INotificationService.cs`, `FileSorter.Core/Abstractions/IAutostartService.cs`, `FileSorter.Core.Tests/MainViewModelAutoTests.cs`
- Modify: `FileSorter.Core/ViewModels/MainViewModel.cs`, `FileSorter.Core/ViewModels/FolderViewModel.cs`, `FileSorter.Core.Tests/Fakes.cs`, `FileSorter/Views/MainWindow.xaml` (карточка Auto-organize, индикатор у папок), `FileSorter/App.xaml.cs` (временные заглушки сервисов до Task 11)

**Interfaces:**
- Consumes: всё из Task 9; `OperationsUndone` (Task 6).
- Produces:
  - `interface INotificationService { void ShowAutoOrganized(string title, string message, Action onUndo); }`
  - `interface IAutostartService { bool IsEnabled { get; } void SetEnabled(bool enabled); }`
  - конструктор `MainViewModel(IUiDispatcher, IDialogService, IThumbnailProvider, IThemeService, INotificationService, IAutostartService)`
  - `MainViewModel`: `bool AutoEnabled`, `bool AutoOnNewFiles`, `bool AutoScheduleEnabled`, `int AutoScheduleInterval`, `ScheduleUnit AutoScheduleUnit`, `bool AutoIsPaused`, `ICommand ResumeAutoCommand`, `IReadOnlyList<string> AutoWarnings` (из `ProfileConflicts`), `bool PauseAll`, `ICommand TogglePauseAllCommand`, `bool StartWithWindows`, `bool KeepRunningInBackground` (= есть папка с `Auto.Enabled`), `bool TrayHintShown`, `void Shutdown()` (сохранить настройки, `Dispose` движка).
  - `OrganizeCommand`/`UndoCommand` недоступны, пока `FolderGate` занят для любой из целевых папок.

- [ ] **Step 1: Падающие тесты**
  - `UndoOperationOfAutoFolder_PausesItsAuto`; `ResumeAuto_ClearsPause`.
  - `ResumeAll_KeepsPerFolderPauses`: папка A на паузе, `PauseAll` вкл → выкл → A всё ещё на паузе.
  - `AutoRunCompleted_NotifiesWithFolderNameAndCount`: сообщение `"Downloads: 3 files organized"`; вызов `onUndo` отменяет операцию.
  - `AutoRunInSelectedFolder_RefreshesFiles`.
  - `KeepRunningInBackground_FollowsAutoEnabled` (PropertyChanged поднимается).
  - `StartWithWindows_CallsAutostartService`.
  - `AutoWarnings_DestinationInsideOtherAutoFolder`.

- [ ] **Step 2: FAIL. Step 3: Реализовать.** `RunCompleted` → `NotificationThrottle.Add`; накопленное сбрасывается `Tick` из цикла `Task.Delay(NotificationInterval)` в VM, пока есть накопленное. Любое изменение авто-настроек/пауз → `engine.Configure(...)` + `SaveSettings()` + `FolderViewModel.RefreshStatus`. Карточка (решение исполнителя): в левой панели между папками и правилами — переключатель «Auto-organize», «On new files», «Every [N] [Minutes|Hours|Days]», строка «Paused» + «Resume», предупреждения, «Start with Windows». Индикатор у папки: точка — зелёная/жёлтая/нет.

- [ ] **Step 4: PASS; сборка.**

- [ ] **Step 5: Commit** `feat: auto-organize settings, pause and notifications in UI`

---

### Task 11: Платформа: трей, уведомления, автозапуск, один экземпляр

**Files:**
- Create: `FileSorter/Platform/TrayIcon.cs`, `FileSorter/Platform/ToastNotificationService.cs`, `FileSorter/Platform/RegistryAutostartService.cs`, `FileSorter/Platform/SingleInstance.cs`
- Modify: `FileSorter/FileSorter.csproj` (`<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>`, `<UseWindowsForms>true</UseWindowsForms>`, `<Using Remove="System.Windows.Forms" />`, `Microsoft.Toolkit.Uwp.Notifications` 7.1.3), `FileSorter/App.xaml.cs`, `FileSorter/Views/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `INotificationService`, `IAutostartService`, `MainViewModel.KeepRunningInBackground/PauseAll/TogglePauseAllCommand/TrayHintShown/Shutdown` (Task 10).
- Produces:
  - `TrayIcon : IDisposable { bool Visible; bool PausedAll; event Action? OpenRequested, PauseAllToggled, ExitRequested; void ShowHint(string title, string text); }` — `NotifyIcon` с иконкой `app.ico`, меню «Open», «Pause all»/«Resume», «Exit», двойной клик = Open.
  - `ToastNotificationService : INotificationService` — toast с кнопкой «Undo»; активация (`ToastNotificationManagerCompat.OnActivated`) приходит на фоновом потоке → `Dispatcher.InvokeAsync(onUndo)`; колбэки хранятся по id в словаре.
  - `RegistryAutostartService : IAutostartService` — `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, значение `FileSorter` = `"<Environment.ProcessPath>" --tray`.
  - `SingleInstance { static bool TryAcquire(Action onActivateRequested); }` — именованный `Mutex` `FileSorter.SingleInstance` + `EventWaitHandle` для сигнала «покажи окно».
  - `App.ExitApplication()`.

- [ ] **Step 1: Реализовать.** `OnStartup`: `SingleInstance.TryAcquire` неуспешен → сигнал и `Shutdown()`. `--tray` и `KeepRunningInBackground` → окно не показывается (создаётся при Open); `--tray` без авто-папок → окно показывается как обычно. `MainWindow.Closing`: если это не смена темы, `KeepRunningInBackground` и не `ExitApplication` → `e.Cancel`, `Hide()`, при `!TrayHintShown` — `ShowHint("FileSorter", "FileSorter keeps running in the tray")` и `TrayHintShown = true`. `Visible` трея следует за `KeepRunningInBackground`. Exit → `vm.Shutdown()`, `TrayIcon.Dispose()`, `Shutdown()`. `SwitchTheme` при скрытом окне не вызывается (кнопка темы есть только в окне).

- [ ] **Step 2: Сборка — 0 warnings, 0 errors; все тесты PASS.**

- [ ] **Step 3: Ручная проверка (Windows):**
  - без авто-папок крестик закрывает программу, иконки в трее нет;
  - включить авто для тестовой папки → крестик прячет в трей, подсказка один раз;
  - скопировать файл в корень → через ~30 с он разложен, toast «…: 1 files organized» с «Undo»; Undo возвращает файл и ставит папку на паузу;
  - «Pause all» / «Resume» в меню трея;
  - «Start with Windows» создаёт/удаляет значение в `Run`; запуск с `--tray` стартует без окна;
  - single instance: второй запуск показывает окно первого и завершается;
  - Firefox-загрузка большого файла в авто-папку не ломается.

- [ ] **Step 4: Commit** `feat: tray, toast notifications, autostart, single instance`

---

### Task 12: Документация

**Files:**
- Modify: `CLAUDE.md`, `TODO.md`

- [ ] **Step 1:** `CLAUDE.md` — профили папок и миграция settings v2, планировщик (`OrganizeRequest`/`PlannedGroup`), правила с назначением, журнал копий, движок автосортировки (UI-поток, `FolderGate`), трей/toast/автозапуск/один экземпляр, тестовый проект и команда `dotnet test`, раздельные лимиты истории и отмена по запускам.
- [ ] **Step 2:** `TODO.md` — пункты 1–4 отмечены `[x]`.
- [ ] **Step 3: Commit** `docs: update CLAUDE.md and TODO.md`
