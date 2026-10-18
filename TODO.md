# TODO

Gap analysis vs. Easy File Organizer (Qiplex). New logic goes into `FileSorter.Core`.

## In progress

- [x] **0. Extract `FileSorter.Core`** (`net10.0`, no WPF) — models, services, view models; platform services behind interfaces
- [ ] **1. Auto-organize** — `FileSystemWatcher` + schedule (minutes/hours/days), tray icon, start with Windows, per-folder settings and on/off indicator
- [ ] **2. Multiple folders at once** — select several folders, organize in one click
- [ ] **3. Rules with a destination** — each rule: conditions + source folder + destination folder (instead of filter-only rules); rules/templates can be toggled per folder. Design together with #1: per-folder settings hold rules, template and schedule
- [ ] **4. Group by common name part** — `hello-world-1.jpg`, `hello-world-2.jpg` → `hello-world` (e.g. `#prefix` template token)

## Backlog

- [ ] **5. Batch method** — split into chunks of 10/100/1000 (`1-100`, `101-200`, …)
- [ ] **6. Organize by file content** — rule field `Content` is currently a stub that matches the file name; implement or remove
- [ ] **7. Photo tags** — beyond date taken: camera, location/country, keywords
- [ ] **8. Click on a donut chart segment** shows only that group
- [ ] **9. Localization** (at least RU/EN)
- [ ] **10. Cross-platform** — macOS/Linux UI on Avalonia on top of `FileSorter.Core`; macOS thumbnails via QuickLook; signing/notarization
