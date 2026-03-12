# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

```bash
# From the FileSorter/FileSorter directory (where .csproj lives):
dotnet build
dotnet run
```

This is a WPF app (`UseWPF`, `WinExe`) targeting .NET 9. Must be built/run on Windows with the Windows Desktop SDK — cannot run from WSL directly.

## Architecture

Single-project WPF application using MVVM pattern (no framework, hand-rolled). Solution file at root: `FileSorter.sln`.

- **MVVM base classes** — `ObservableObject` (INotifyPropertyChanged), `RelayCommand`/`AsyncRelayCommand` (ICommand) in `ViewModels/`
- **`MainViewModel`** — central ViewModel; orchestrates scanning, filtering, preview, organize, history, and theme operations. Directly instantiates services (no DI).
- **Services:**
  - `FileOrganizerService` — scans folders (async with per-file batching), groups files by method (type/extension/date/alphabet/size/date-taken) or by template pattern, performs move/copy, undo via stack, maintains operation history
  - `FileCategoryService` — static file-extension-to-category mapping (Documents, Images, Videos, etc.)
  - `PhotoMetadataService` — reads EXIF DateTaken from image files using WPF BitmapMetadata; loaded lazily on demand (only when ByDateTaken or `#datetaken` template is used), not during scanning
  - `SettingsService` — persists `AppSettings` as JSON in `%LocalAppData%/FileSorter/settings.json`
- **Models:** `FileItem` (includes `DateTakenLoaded` for lazy EXIF), `FileCategory`, `OrganizeMethod` (enum), `OrganizeOperation`/`FileMove` (undo/history tracking), `FilterRule`/`FilterRuleSnapshot` (thread-safe rule evaluation), `PreviewItem`, `RecentFolder`, `CategoryStats`, `DateGranularity`
- **Views:** `MainWindow.xaml` — single window app, DataContext set from `App.xaml.cs`
- **Themes:** `DarkTheme.xaml` and `LightTheme.xaml` — switched at runtime by recreating the MainWindow with the same ViewModel. Includes full custom ControlTemplates for ComboBox (toggle button, dropdown popup, items), ScrollBar (thin thumb, no arrows), and TreeView highlight removal.
- **Controls:** `DonutChart` — custom WPF control for category stats visualization, reads theme colors via `FindResource`

## Key Patterns

- **Async preview rendering** — `RefreshPreviewAsync()` runs all heavy work (filtering, `GeneratePreview`, stats calculation) on a background thread via `Task.Run`; only the final ObservableCollection updates happen on the UI thread. A `_refreshVersion` counter discards stale results when the user clicks faster than computation completes.
- **Per-file scanning** — `ScanFolderAsync` uses batchSize=1 with `dispatcher.InvokeAsync` callback; each file triggers `RefreshPreview` for real-time chart updates without blocking the UI.
- **Lazy EXIF loading** — `DateTaken` is NOT read during scanning. `FileItem.DateTakenLoaded` tracks whether EXIF was fetched. `EnsureDateTakenLoaded()` is called only when grouping by DateTaken or using `#datetaken` template token.
- **Thread-safe rule snapshots** — `FilterRuleSnapshot` (immutable record) captures rule state for background evaluation, avoiding cross-thread access to `FilterRuleViewModel` properties.
- Organize grouping strategies: method-based (switch on `OrganizeMethod`) or template-based (`#type`, `#ext`, `#year`, `#month`, `#date`, `#datetaken`, `#alpha`, `#size` tokens in `DestinationSubfolderPattern`)
- Multi-level subfolder support: template tokens separated by `/` create nested folder hierarchies
- Filtering has two layers: category toggles and user-defined rules (AND/OR mode)
- Undo is operation-based: each organize pushes an `OrganizeOperation` onto a stack; undo reverses moves or deletes copies
- Theme switching: `App.SwitchTheme()` swaps the merged ResourceDictionary and recreates the MainWindow, preserving the ViewModel state
- Window uses `WindowStyle="None"` + `WindowChrome` (no `AllowsTransparency` — it kills WPF rendering performance via software rendering)
- TreeViewItem selection highlight disabled via SystemColors resource overrides
- C# with nullable reference types and implicit usings enabled

## Performance Notes

- **Never use `AllowsTransparency="True"`** — it disables hardware acceleration and causes the entire window to render via CPU. Rounded corners are not worth the performance cost.
- Keep heavy computation off the UI thread — use `Task.Run` + version counter pattern (see `RefreshPreviewAsync`).
- EXIF reading (`BitmapFrame.Create`) is expensive I/O — always lazy-load, never during scanning.
