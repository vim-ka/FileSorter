# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

```bash
# From the repo root:
dotnet build FileSorter.sln
dotnet run --project FileSorter
dotnet test FileSorter.Core.Tests
```

This is a WPF app (`UseWPF`, `WinExe`) targeting .NET 10. Must be built/run on Windows with the Windows Desktop SDK — cannot run from WSL directly.

## Architecture

MVVM (no framework, hand-rolled). Solution file at root: `FileSorter.sln`, three projects:

- **`FileSorter.Core`** (`net10.0`, no WPF — keep it that way, it's the base for a future Avalonia/macOS UI): `Models/`, `Services/`, `ViewModels/`, `Abstractions/`. Platform-specific needs go through interfaces in `Abstractions/` (`IUiDispatcher`, `IDialogService`, `IThumbnailProvider`, `IThemeService`, `INotificationService`, `IAutostartService`); colors are `RgbColor`, thumbnails are opaque `object`s. Commands don't rely on WPF `CommandManager` — `MainViewModel` raises `CanExecuteChanged` explicitly.
- **`FileSorter.Core.Tests`** (xunit, `net10.0`): tests for Core. `JsonFileStore.DataDir` is settable so tests redirect settings/history away from `%LocalAppData%` (see `TestData`); `TempDataDirTest` is the base class that does this per test. `MainViewModel` is driven with fake `IUiDispatcher`/`IDialogService` implementations (`Fakes.cs`: `VmHost` + `SingleThreadDispatcher` run VM tests on a real sync context).
- **`FileSorter`** (WPF + WinForms `NotifyIcon`, `net10.0-windows10.0.17763.0`): `App`, `Views/`, `Themes/`, `Controls/`, `Converters/`, `Platform/` (WPF/Windows implementations of the Core abstractions, Shell thumbnails, tray icon, toasts via `Microsoft.Toolkit.Uwp.Notifications`, `HKCU\...\Run` autostart, single instance). `App.OnStartup` wires them into `MainViewModel`. XAML references Core namespaces with `;assembly=FileSorter.Core`.

- **MVVM base classes** — `ObservableObject` (INotifyPropertyChanged), `RelayCommand`/`AsyncRelayCommand` (ICommand) in `ViewModels/`
- **`MainViewModel`** — central ViewModel, split into partials (`MainViewModel.cs`, `MainViewModel.Auto.cs` for auto-organize/pause/notification wiring); orchestrates scanning, filtering, preview, organize, history, and theme operations. Directly instantiates services (no DI).
- **Services:**
  - `FileOrganizerService` — scans folders (async with per-file batching), `Plan` groups files into `PlannedGroup`s from an `OrganizeRequest` (routing rules first, then method or template), `GeneratePreview` renders them, `OrganizeAsync`/`UndoOperationAsync` perform move/copy and undo, maintains operation history
  - `FileCategoryService` — static file-extension-to-category mapping (Documents, Images, Videos, etc.)
  - `PhotoMetadataService` — reads EXIF DateTaken via the MetadataExtractor package; loaded lazily on demand (only when ByDateTaken or `#datetaken` template is used), not during scanning
  - `PreviewBuilder` — multi-folder preview (one `OrganizeRequest` per checked folder, merged); `PathKey` — normalized, case-insensitive path comparison
  - `CopyJournal` — persistent record (`copy-journal.json`) of source files already copied (path+size+mtime) so repeated copy runs skip unchanged files
  - `AutoOrganizeEngine` — `FileSystemWatcher` + schedules (debounce 30 s, file-stability window 30 s, skips `.crdownload`/`.part`/`.partial`/`.tmp`/`.download`/`.opdownload`); runs on the UI thread via `IUiDispatcher`, heavy work off-thread. Helpers: `FolderGate` (one organize/undo per source folder), `ProfileConflicts` (overlapping profiles), `NotificationThrottle` (at most one notification per folder per 30 s), `AutoOrganizeCandidates`
  - `SettingsService` / `HistoryService` — persist `AppSettings` (v2: per-folder `FolderProfile`s, migrated from the old `RecentFolders` settings) and the history (50 manual + 200 auto `OrganizeOperation`s, counted separately) as JSON in `%LocalAppData%/FileSorter/` via `JsonFileStore` (atomic temp-file write, corrupt file renamed to `*.bak`, enums as strings)
  - `ThumbnailService` — Windows Shell thumbnails (`IShellItemImageFactory`), alpha-preserving HBITMAP conversion
  - `ThumbnailLoader` — dedicated STA thread + LRU cache (path+mtime); thumbnails are requested lazily when a `ViewFileItem.Thumbnail` binding is first read, i.e. only for visible rows of the virtualized list
- **Models:** `FileItem` (includes `DateTakenLoaded` for lazy EXIF), `FileCategory`, `OrganizeMethod` (enum), `OrganizeOperation`/`FileMove`/`RunContext` (undo/history tracking; `RunId`, `IsAuto`), `FolderProfile` (per-folder method/template/filters/destination/copy mode/rules/`UnmatchedFilesMode`/`AutoOrganizeSettings`), `FilterRule`/`FilterRuleSnapshot` (one condition of a routing rule; thread-safe evaluation), `RoutingRule`/`RoutingRuleSnapshot` (conditions + destination), `PreviewItem`, `CategoryStats`, `DateGranularity`
- **Views (WPF project):** `MainWindow.xaml` — single window app, DataContext set from `App.xaml.cs`
- **Themes:** `DarkTheme.xaml` and `LightTheme.xaml` — switched at runtime by recreating the MainWindow with the same ViewModel. Includes full custom ControlTemplates for ComboBox (toggle button, dropdown popup, items), ScrollBar (thin thumb, no arrows), and TreeView highlight removal.
- **Controls:** `DonutChart` — custom WPF control for category stats visualization, reads theme colors via `FindResource`

## Key Patterns

- **Async preview rendering** — `RefreshPreviewAsync()` runs all heavy work (filtering, `GeneratePreview`, stats calculation) on a background thread via `Task.Run`; only the final ObservableCollection updates happen on the UI thread. A `_refreshVersion` counter discards stale results when the user clicks faster than computation completes.
- **Batched scanning** — `ScanFolderAsync` reports batches of 50 via `dispatcher.InvokeAsync`; `RefreshPreviewThrottled` refreshes the preview at most every 200ms during the scan. The `_duplicates` folder is excluded from scanning.
- **Lazy EXIF loading** — `DateTaken` is NOT read during scanning. `FileItem.DateTakenLoaded` tracks whether EXIF was fetched. `EnsureDateTakenLoaded()` is called only when grouping by DateTaken or using `#datetaken` template token.
- **Thread-safe rule snapshots** — `FilterRuleSnapshot`/`RoutingRuleSnapshot` (immutable records) capture rule state for background evaluation, avoiding cross-thread access to view-model properties. `OrganizeRequest.FromProfile` builds the immutable request the planner uses.
- Organize grouping strategies: method-based (switch on `OrganizeMethod`) or template-based (`#type`, `#ext`, `#year`, `#month`, `#date`, `#datetaken`, `#alpha`, `#size`, `#prefix` tokens in `DestinationSubfolderPattern`)
- Multi-level subfolder support: template tokens separated by `/` create nested folder hierarchies
- `#prefix` / `OrganizeMethod.ByName` group files by common name part (`NamePrefix`, e.g. `hello-world-1.jpg` → `hello-world`)
- File selection: category toggles (when filters are applied) decide which files take part; routing rules (each with All/Any conditions) and "Leave in place" for unmatched files decide where they go
- Per-folder profiles: each folder keeps its own `FolderProfile` in `AppSettings`; multi-folder organize runs every checked folder with its own profile
- Checked vs selected: only checked folders are scanned, previewed and organized (`OrganizeTargets`); the selected folder is just the one whose profile the settings panel edits. Adding a folder checks and selects it; checking a folder while none is selected selects it
- Routing rules: per-profile conditions + destination (empty = the rule only selects files, which are then organized by the method/template like unmatched ones); first match wins, unmatched files follow `UnmatchedFilesMode` (organize normally / leave in place). A rule is inert (`Snapshot()` null, not counted for RULES ON) if a condition can't be evaluated: blank value, unparsable days/size (size = MB, `.` or `,` decimal), or an operator the field doesn't support (dates: only "last N days"). A rule without conditions matches every file, so the UI never removes a rule's last condition
- Undo is run-based: each organize run has a `RunId` (`RunContext`, `IsAuto`) and appends one `OrganizeOperation` per folder (recorded even if the run fails midway or is cancelled). `OrganizeAsync` takes a `CancellationToken` checked between files only; on exit `Shutdown` cancels manual and auto runs and `CompleteInFlightRuns` waits (≤ 5 s, background loops only — no UI-thread deadlock) and writes their history/journal synchronously. Undo reverts the `LastUndoableRun`; any operation can be undone unless a later active operation moved its results (`CanUndoOperation`). An undo pauses auto-organize for that folder first. Per-move `IsUndone` allows partial undo/retry; copies are deleted only if size+mtime still match.
- Auto-organize: per-profile `AutoOrganizeSettings` (enabled, on new files, schedule in minutes/hours/days, `IsPaused`); auto runs are flagged `IsAuto`, are undoable, and raise toast notifications (at most one per folder per 30 s, `NotificationThrottle`; no toasts if registration fails). The tray icon is shown, and closing the window keeps the app running, only while some folder has auto on (`--tray` from autostart, single instance; autostart only ever registers `FileSorter.exe`).
  - The engine ignores files it placed into an auto-enabled root (`_placed`: path+size+mtime) until they change, so folders don't ping-pong. Copy-mode runs prune journal entries of files gone from the root and wait for the journal load (`CopyJournal.WhenLoaded`) before choosing candidates. Candidates are planned first; only files the plan really moves are lock-checked.
  - Editing the selected folder's profile calls `AutoOrganizeEngine.Defer` (pending/scheduled runs wait one debounce after the last edit, so half-typed values are never used) and saves settings after 1.5 s of quiet. Failed or missing watchers are rebuilt at the next run or schedule tick.
- Skip duplicates: size-bucketed SHA-256 (`DuplicateIndex`). Copy mode skips duplicates; move mode moves them to `<target>/_duplicates` (undoable). Never delete user files.
- Theme switching: `App.SwitchTheme()` swaps the merged ResourceDictionary and recreates the MainWindow, preserving the ViewModel state
- Window uses `WindowStyle="None"` + `WindowChrome` (no `AllowsTransparency` — it kills WPF rendering performance via software rendering)
- TreeViewItem selection highlight disabled via SystemColors resource overrides
- C# with nullable reference types and implicit usings enabled

## Performance Notes

- **Never use `AllowsTransparency="True"`** — it disables hardware acceleration and causes the entire window to render via CPU. Rounded corners are not worth the performance cost.
- Keep heavy computation off the UI thread — use `Task.Run` + version counter pattern (see `RefreshPreviewAsync`).
- EXIF reading is expensive I/O — always lazy-load, never during scanning.
