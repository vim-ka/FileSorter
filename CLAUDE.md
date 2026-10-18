# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

```bash
# From the repo root:
dotnet build FileSorter.sln
dotnet run --project FileSorter
```

This is a WPF app (`UseWPF`, `WinExe`) targeting .NET 10. Must be built/run on Windows with the Windows Desktop SDK — cannot run from WSL directly.

## Architecture

MVVM (no framework, hand-rolled). Solution file at root: `FileSorter.sln`, two projects:

- **`FileSorter.Core`** (`net10.0`, no WPF — keep it that way, it's the base for a future Avalonia/macOS UI): `Models/`, `Services/`, `ViewModels/`, `Abstractions/`. Platform-specific needs go through interfaces in `Abstractions/` (`IUiDispatcher`, `IDialogService`, `IThumbnailProvider`, `IThemeService`); colors are `RgbColor`, thumbnails are opaque `object`s. Commands don't rely on WPF `CommandManager` — `MainViewModel` raises `CanExecuteChanged` explicitly.
- **`FileSorter`** (WPF, `net10.0-windows`): `App`, `Views/`, `Themes/`, `Controls/`, `Converters/`, `Platform/` (WPF/Windows implementations of the Core abstractions, Shell thumbnails). `App.OnStartup` wires them into `MainViewModel`. XAML references Core namespaces with `;assembly=FileSorter.Core`.

- **MVVM base classes** — `ObservableObject` (INotifyPropertyChanged), `RelayCommand`/`AsyncRelayCommand` (ICommand) in `ViewModels/`
- **`MainViewModel`** — central ViewModel; orchestrates scanning, filtering, preview, organize, history, and theme operations. Directly instantiates services (no DI).
- **Services:**
  - `FileOrganizerService` — scans folders (async with per-file batching), groups files by method (type/extension/date/alphabet/size/date-taken) or by template pattern, performs move/copy, undo via stack, maintains operation history
  - `FileCategoryService` — static file-extension-to-category mapping (Documents, Images, Videos, etc.)
  - `PhotoMetadataService` — reads EXIF DateTaken via the MetadataExtractor package; loaded lazily on demand (only when ByDateTaken or `#datetaken` template is used), not during scanning
  - `SettingsService` / `HistoryService` — persist `AppSettings` and the last 50 `OrganizeOperation`s as JSON in `%LocalAppData%/FileSorter/` via `JsonFileStore` (atomic temp-file write, corrupt file renamed to `*.bak`, enums as strings)
  - `ThumbnailService` — Windows Shell thumbnails (`IShellItemImageFactory`), alpha-preserving HBITMAP conversion
  - `ThumbnailLoader` — dedicated STA thread + LRU cache (path+mtime); thumbnails are requested lazily when a `ViewFileItem.Thumbnail` binding is first read, i.e. only for visible rows of the virtualized list
- **Models:** `FileItem` (includes `DateTakenLoaded` for lazy EXIF), `FileCategory`, `OrganizeMethod` (enum), `OrganizeOperation`/`FileMove` (undo/history tracking), `FilterRule`/`FilterRuleSnapshot` (thread-safe rule evaluation), `PreviewItem`, `RecentFolder`, `CategoryStats`, `DateGranularity`
- **Views (WPF project):** `MainWindow.xaml` — single window app, DataContext set from `App.xaml.cs`
- **Themes:** `DarkTheme.xaml` and `LightTheme.xaml` — switched at runtime by recreating the MainWindow with the same ViewModel. Includes full custom ControlTemplates for ComboBox (toggle button, dropdown popup, items), ScrollBar (thin thumb, no arrows), and TreeView highlight removal.
- **Controls:** `DonutChart` — custom WPF control for category stats visualization, reads theme colors via `FindResource`

## Key Patterns

- **Async preview rendering** — `RefreshPreviewAsync()` runs all heavy work (filtering, `GeneratePreview`, stats calculation) on a background thread via `Task.Run`; only the final ObservableCollection updates happen on the UI thread. A `_refreshVersion` counter discards stale results when the user clicks faster than computation completes.
- **Batched scanning** — `ScanFolderAsync` reports batches of 50 via `dispatcher.InvokeAsync`; `RefreshPreviewThrottled` refreshes the preview at most every 200ms during the scan. The `_duplicates` folder is excluded from scanning.
- **Lazy EXIF loading** — `DateTaken` is NOT read during scanning. `FileItem.DateTakenLoaded` tracks whether EXIF was fetched. `EnsureDateTakenLoaded()` is called only when grouping by DateTaken or using `#datetaken` template token.
- **Thread-safe rule snapshots** — `FilterRuleSnapshot` (immutable record) captures rule state for background evaluation, avoiding cross-thread access to `FilterRuleViewModel` properties.
- Organize grouping strategies: method-based (switch on `OrganizeMethod`) or template-based (`#type`, `#ext`, `#year`, `#month`, `#date`, `#datetaken`, `#alpha`, `#size` tokens in `DestinationSubfolderPattern`)
- Multi-level subfolder support: template tokens separated by `/` create nested folder hierarchies
- Filtering has two layers: category toggles and user-defined rules (AND/OR mode)
- Undo is operation-based: each organize appends an `OrganizeOperation` (recorded even if the run fails midway). Any operation can be undone unless a later active operation moved its results (`CanUndoOperation`). Per-move `IsUndone` allows partial undo/retry; copies are deleted only if size+mtime still match.
- Skip duplicates: size-bucketed SHA-256 (`DuplicateIndex`). Copy mode skips duplicates; move mode moves them to `<target>/_duplicates` (undoable). Never delete user files.
- Theme switching: `App.SwitchTheme()` swaps the merged ResourceDictionary and recreates the MainWindow, preserving the ViewModel state
- Window uses `WindowStyle="None"` + `WindowChrome` (no `AllowsTransparency` — it kills WPF rendering performance via software rendering)
- TreeViewItem selection highlight disabled via SystemColors resource overrides
- C# with nullable reference types and implicit usings enabled

## Performance Notes

- **Never use `AllowsTransparency="True"`** — it disables hardware acceleration and causes the entire window to render via CPU. Rounded corners are not worth the performance cost.
- Keep heavy computation off the UI thread — use `Task.Run` + version counter pattern (see `RefreshPreviewAsync`).
- EXIF reading is expensive I/O — always lazy-load, never during scanning.
