# TODO

Gap analysis vs. Easy File Organizer (Qiplex). New logic goes into `FileSorter.Core`.

## In progress

- [x] **0. Extract `FileSorter.Core`** (`net10.0`, no WPF) — models, services, view models; platform services behind interfaces
- [x] **1. Auto-organize** — `FileSystemWatcher` + schedule (minutes/hours/days), tray icon, start with Windows, per-folder settings and on/off indicator
- [x] **2. Multiple folders at once** — select several folders, organize in one click
- [x] **3. Rules with a destination** — each rule: conditions + source folder + destination folder (instead of filter-only rules); rules/templates can be toggled per folder. Design together with #1: per-folder settings hold rules, template and schedule
- [x] **4. Group by common name part** — `hello-world-1.jpg`, `hello-world-2.jpg` → `hello-world` (e.g. `#prefix` template token)

## Backlog

- [ ] **5. Batch method** — split into chunks of 10/100/1000 (`1-100`, `101-200`, …)
- [ ] **6. Organize by file content** — rule field `Content` is currently a stub that matches the file name; implement or remove
- [ ] **7. Photo tags** — beyond date taken: camera, location/country, keywords
- [ ] **8. Click on a donut chart segment** shows only that group
- [ ] **9. Localization** (at least RU/EN)
- [ ] **10. Cross-platform** — macOS/Linux UI on Avalonia on top of `FileSorter.Core`; macOS thumbnails via QuickLook; signing/notarization
- [ ] **11. Rename while organizing** — not in Easy File Organizer (Qiplex sells renaming as a separate Smart File Renamer). A "File name" template in the Destination block, under Subfolders:
  - tokens: `#name` (original name), `#date` / `#datetaken`, `#n` (counter within the target folder), `#folder` (source folder name), plus `#type` `#ext` `#year` `#month`; e.g. `#datetaken_#n` → `2026-10-15_001.jpg`
  - empty = keep the name; the preview shows the new names; undo restores place and name (history already stores full old/new paths)
  - to decide: name clashes (append `(2)` vs. always number); a per-rule name template or one per folder
- [ ] **12. Redesign the Auto-organize block** — today it's a bare header with an unlabeled checkbox on the right plus "Start with Windows"; looks unfinished. Study how Easy File Organizer lays out its auto-organize/schedule settings (toggle switch, labeled mode "on new files / on schedule", interval, status) and rework ours to match the new Rules/Destination card style
