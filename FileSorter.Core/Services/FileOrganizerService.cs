using System.Collections.ObjectModel;
using System.Security.Cryptography;
using FileSorter.Models;

namespace FileSorter.Services;

public class FileOrganizerService
{
    /// <summary>
    /// Duplicates found in Move mode are moved here (inside the target folder) instead of being
    /// deleted, so they stay recoverable via Undo. The folder is excluded from scanning.
    /// </summary>
    public const string DuplicatesFolderName = "_duplicates";

    private readonly List<OrganizeOperation> _history = [];

    private readonly CopyJournal _journal;

    public FileOrganizerService() : this(new CopyJournal()) { }

    public FileOrganizerService(CopyJournal journal) => _journal = journal;

    public CopyJournal Journal => _journal;

    public IReadOnlyList<OrganizeOperation> History => _history;

    private Task? _historyLoad;

    /// <summary>
    /// Loads persisted history. Operations that mutate history wait for a pending load first,
    /// so an organize started right after launch is never overwritten by the loaded list.
    /// </summary>
    public Task LoadHistoryAsync() => _historyLoad = LoadHistoryCoreAsync();

    private async Task LoadHistoryCoreAsync()
    {
        var journalLoad = _journal.ReloadAsync();
        var loaded = await HistoryService.LoadAsync();
        _history.Clear();
        _history.AddRange(loaded);
        await journalLoad;
    }

    private Task EnsureHistoryLoadedAsync() => _historyLoad ?? Task.CompletedTask;

    /// <summary>Organize runs whose file loop has started and that are not yet recorded by their own continuation.</summary>
    private readonly List<InFlightRun> _inFlight = [];

    private sealed class InFlightRun(OrganizeOperation operation, Func<string> describe)
    {
        public OrganizeOperation Operation { get; } = operation;
        public Func<string> Describe { get; } = describe;
        /// <summary>The background file loop; never needs the UI thread to finish.</summary>
        public Task Work { get; set; } = Task.CompletedTask;
        public bool Recorded;
        public volatile bool JournalChanged;
    }

    /// <summary>Adds the run's operation to history once (if it did anything); true when it was added now.</summary>
    private bool TryRecord(InFlightRun run)
    {
        lock (_inFlight)
        {
            if (run.Recorded) return false;
            run.Recorded = true;
            run.Operation.Description = run.Describe();
            if (run.Operation.Moves.Count == 0) return false;
            _history.Add(run.Operation);
            TrimHistory();
            return true;
        }
    }

    /// <summary>
    /// For exit, on the UI thread after the runs' cancellation was requested: waits (bounded) for
    /// the background file loops of in-flight organize runs, then records their operations and
    /// writes history and the copy journal synchronously. Their own continuations (which need the
    /// UI thread, blocked here) would run too late. Only background completion is awaited, so this
    /// can't deadlock. False when some run didn't finish in time (it is not recorded).
    /// </summary>
    public bool CompleteInFlightRuns(TimeSpan timeout)
    {
        InFlightRun[] runs;
        lock (_inFlight) runs = [.. _inFlight];

        var allDone = true;
        if (runs.Length > 0)
        {
            try { allDone = Task.WaitAll(runs.Select(r => r.Work).ToArray(), timeout); }
            catch (AggregateException) { /* a loop failed: it is recorded like any failed run */ }
        }

        var added = false;
        var journalChanged = false;
        foreach (var run in runs.Where(r => r.Work.IsCompleted))
        {
            added |= TryRecord(run);
            journalChanged |= run.JournalChanged;
        }

        // History loaded (or nothing would be lost by not writing): never overwrite the file with a partial list
        if ((added || HistoryService.HasUnwrittenSaves) && _historyLoad is not { IsCompleted: false })
        {
            List<OrganizeOperation> snapshot;
            lock (_inFlight) snapshot = [.. _history];
            HistoryService.Save(snapshot);
        }
        if (journalChanged)
            _journal.SaveNow();
        return allDone;
    }

    public async Task ClearHistoryAsync()
    {
        await EnsureHistoryLoadedAsync();
        _history.Clear();
        await HistoryService.SaveAsync(_history);
    }

    /// <summary>
    /// An operation can be undone unless a later, still active operation moved files out of
    /// the places this one put them — undoing it first would leave those files stranded.
    /// </summary>
    public bool CanUndoOperation(OrganizeOperation operation)
    {
        if (operation.IsUndone) return false;

        var index = _history.IndexOf(operation);
        if (index < 0) return false;

        var laterSources = _history
            .Skip(index + 1)
            .Where(op => !op.IsUndone)
            .SelectMany(op => op.Moves)
            .Where(m => !m.IsUndone && !m.WasCopied)
            .Select(m => m.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return laterSources.Count == 0
            || !operation.Moves.Any(m => !m.IsUndone && laterSources.Contains(m.DestinationPath));
    }

    /// <summary>
    /// The still-undoable operations of the most recent run that has any (oldest first).
    /// Legacy operations without a RunId each form a run of their own. Empty when nothing can be undone.
    /// </summary>
    public IReadOnlyList<OrganizeOperation> LastUndoableRun
    {
        get
        {
            var last = _history.LastOrDefault(CanUndoOperation);
            if (last == null) return [];
            if (last.RunId == Guid.Empty) return [last];
            return _history.Where(op => op.RunId == last.RunId && CanUndoOperation(op)).ToList();
        }
    }

    /// <summary>
    /// Scans folder in background. Calls onBatch on UI thread for each batch, awaiting completion
    /// before continuing (provides backpressure so UI doesn't get flooded).
    /// </summary>
    public async Task ScanFolderAsync(string folderPath, bool includeSubfolders,
        Func<List<FileItem>, Task> onBatch, int batchSize, CancellationToken ct)
    {
        if (!Directory.Exists(folderPath)) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,   // an unreadable subfolder must not abort the whole scan
            AttributesToSkip = 0         // include hidden/system files, like SearchOption did
        };

        await Task.Run(async () =>
        {
            var batch = new List<FileItem>(batchSize);
            var duplicatesPrefix = FolderPrefix(Path.Combine(folderPath, DuplicatesFolderName));

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                // Paths from EnumerateFiles are already rooted — no per-file GetFullPath needed
                if (filePath.StartsWith(duplicatesPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var info = new FileInfo(filePath);
                    batch.Add(new FileItem
                    {
                        FullPath = filePath,
                        Size = info.Length,
                        DateModified = info.LastWriteTime,
                        DateCreated = info.CreationTime
                    });

                    if (batch.Count >= batchSize)
                    {
                        await onBatch(batch);
                        batch = new List<FileItem>(batchSize);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Skip files we can't access
                }
            }

            // Report remaining files
            if (batch.Count > 0)
                await onBatch(batch);
        }, ct);
    }

    /// <summary>
    /// Decides where every file goes: category filter (if enabled) → first matching routing rule →
    /// otherwise, depending on <see cref="OrganizeRequest.Unmatched"/>, the method/template grouping
    /// or nowhere (left in place). Groups are merged by folder path, ignoring case.
    /// Pure apart from lazy EXIF loading for ByDateTaken / #datetaken — call off the UI thread.
    /// </summary>
    public static IReadOnlyList<PlannedGroup> Plan(IReadOnlyList<FileItem> files, OrganizeRequest request)
    {
        // Canonical target so rule folders (normalized) and method folders share keys
        var target = string.IsNullOrWhiteSpace(request.TargetFolder)
            ? request.TargetFolder
            : PathKey.Normalize(request.TargetFolder);
        var groups = new Dictionary<string, (IReadOnlyList<string> Segments, List<FileItem> Files)>(
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        // Organized by the method/template: unmatched files ("organize normally") and files of
        // rules without a folder (the rule only selects them)
        var normal = new List<FileItem>();

        void Add(string folderPath, IReadOnlyList<string> segments, IEnumerable<FileItem> items)
        {
            if (!groups.TryGetValue(folderPath, out var group))
            {
                groups[folderPath] = group = (segments, []);
                order.Add(folderPath);
            }
            group.Files.AddRange(items);
        }

        foreach (var file in files)
        {
            if (request.AllowedCategories is { } allowed
                && !allowed.Contains(FileCategoryService.GetCategoryName(file.Extension, request.Categories)))
                continue;

            var rule = request.Rules.FirstOrDefault(r => r.Matches(file));
            if (rule is { OrganizesNormally: true })
            {
                normal.Add(file);
            }
            else if (rule != null)
            {
                var folder = rule.ResolveFolder(target);
                Add(folder, RuleDisplaySegments(folder, target), [file]);
            }
            else if (request.Unmatched == UnmatchedFilesMode.OrganizeNormally)
            {
                normal.Add(file);
            }
        }

        foreach (var (key, items) in GroupFiles(normal, request))
        {
            var folder = Path.Combine(target, key.Replace('/', Path.DirectorySeparatorChar));
            Add(folder, key.Split('/', '\\'), items);
        }

        return order.Select(path => new PlannedGroup(path, groups[path].Segments, groups[path].Files)).ToList();
    }

    /// <summary>
    /// Relative folders under the target; the target itself is ".", a folder elsewhere is shown
    /// as one full-path node. With no target, relative folders are shown as they are.
    /// </summary>
    private static IReadOnlyList<string> RuleDisplaySegments(string folder, string target)
    {
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        if (string.IsNullOrWhiteSpace(target))
            return Path.IsPathFullyQualified(folder) ? [folder] : folder.Split(separators);
        if (PathKey.Equal(folder, target))
            return ["."];
        return PathKey.IsUnder(folder, target)
            ? Path.GetRelativePath(target, folder).Split(separators)
            : [folder];
    }

    public static ObservableCollection<PreviewItem> GeneratePreview(IReadOnlyList<FileItem> files, OrganizeRequest request)
    {
        var preview = new ObservableCollection<PreviewItem>();

        foreach (var group in Plan(files, request).OrderBy(g => string.Join('/', g.DisplaySegments)))
        {
            var pathParts = group.DisplaySegments;
            var currentChildren = preview;

            for (int i = 0; i < pathParts.Count; i++)
            {
                var existing = currentChildren.FirstOrDefault(p => p.IsFolder && string.Equals(p.Name, pathParts[i], StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new PreviewItem
                    {
                        Name = pathParts[i],
                        IsFolder = true,
                        Icon = "\uE8B7"
                    };
                    currentChildren.Add(existing);
                }

                if (i == pathParts.Count - 1)
                {
                    existing.FileCount += group.Files.Count;
                    foreach (var file in group.Files.OrderBy(f => f.FileName))
                    {
                        existing.Children.Add(new PreviewItem
                        {
                            Name = file.FileName,
                            IsFolder = false,
                            Icon = "\uE8A5",
                            File = file
                        });
                    }
                }

                currentChildren = existing.Children;
            }
        }

        return preview;
    }

    /// <summary>
    /// Moves or copies the files as planned and records the operation in history (also when the
    /// run fails midway or is cancelled, so whatever was done can be undone). Cancellation is
    /// checked between files, never in the middle of one.
    /// </summary>
    public async Task<OrganizeOperation> OrganizeAsync(
        IReadOnlyList<FileItem> files, OrganizeRequest request, RunContext run, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureHistoryLoadedAsync();

        var copyMode = request.CopyMode;
        var duplicatesFolder = Path.Combine(request.TargetFolder, DuplicatesFolderName);
        var operation = new OrganizeOperation
        {
            RunId = run.RunId,
            IsAuto = run.IsAuto,
            SourceFolder = request.SourceFolder
        };
        if (ct.IsCancellationRequested) return operation;

        var verb = copyMode ? "Copied" : "Moved";
        var by = request.Template != null && request.Template.Contains('#') ? "template" : request.Method.ToString();
        var folderName = FolderProfile.CreateDefault(request.SourceFolder).Name;
        var inFlight = new InFlightRun(operation,
            () => $"{(run.IsAuto ? "Auto · " : "")}{folderName}: {verb} {operation.Moves.Count(m => !m.WasDuplicate)} files by {by}");
        var duplicates = request.SkipDuplicates ? new DuplicateIndex() : null;

        try
        {
            lock (_inFlight)
            {
                inFlight.Work = Task.Run(() =>
                {
                    // Planning may read EXIF (ByDateTaken / #datetaken) — keep it off the UI thread
                    var plan = Plan(files, request);
                    int processed = 0;
                    int total = plan.Sum(g => g.Files.Count);

                    foreach (var group in plan)
                    {
                        if (ct.IsCancellationRequested) return;
                        var folderPath = group.FolderPath;

                        try
                        {
                            Directory.CreateDirectory(folderPath);
                            duplicates?.AddExistingFiles(folderPath);
                        }
                        catch
                        {
                            // Can't create or read the target folder — every file of the group fails
                            operation.FailedFiles += group.Files.Count;
                            processed += group.Files.Count;
                            progress?.Report(processed * 100 / Math.Max(total, 1));
                            continue;
                        }

                        foreach (var file in group.Files)
                        {
                            if (ct.IsCancellationRequested) return;
                            processed++;
                            try
                            {
                                ProcessFile(file, folderPath);
                            }
                            catch
                            {
                                operation.FailedFiles++;
                            }
                            progress?.Report(processed * 100 / Math.Max(total, 1));
                        }
                    }
                });
                _inFlight.Add(inFlight);
            }
            await inFlight.Work;
        }
        finally
        {
            // Record whatever was done, even if something unexpected aborted the loop, so it can
            // still be undone — unless CompleteInFlightRuns (exit) already recorded and saved it.
            var added = TryRecord(inFlight);
            lock (_inFlight) _inFlight.Remove(inFlight);

            if (added)
                await HistoryService.SaveAsync(_history);

            if (inFlight.JournalChanged)
                await _journal.SaveAsync();
        }

        return operation;

        void JournalSource(FileItem file)
        {
            // Record what was actually read, not the scanned snapshot
            var info = new FileInfo(file.FullPath);
            _journal.Add(request.SourceFolder, file.FullPath, info.Length, info.LastWriteTimeUtc);
            inFlight.JournalChanged = true;
        }

        void ProcessFile(FileItem file, string folderPath)
        {
            if (IsUnderFolder(file.FullPath, duplicatesFolder))
                return;

            var destPath = Path.Combine(folderPath, file.FileName);

            // Already where it belongs (re-organizing an organized folder) — leave it alone
            if (PathsEqual(destPath, file.FullPath))
                return;

            if (duplicates != null && duplicates.IsDuplicate(file.FullPath, file.Size))
            {
                operation.SkippedDuplicates++;
                if (copyMode)
                {
                    JournalSource(file);
                    return; // nothing to do — the original stays untouched
                }

                Directory.CreateDirectory(duplicatesFolder);
                var dupPath = GetUniqueFileName(Path.Combine(duplicatesFolder, file.FileName));
                File.Move(file.FullPath, dupPath);
                operation.Moves.Add(new FileMove
                {
                    SourcePath = file.FullPath,
                    DestinationPath = dupPath,
                    WasDuplicate = true
                });
                return;
            }

            destPath = GetUniqueFileName(destPath);
            if (copyMode)
                File.Copy(file.FullPath, destPath, false);
            else
                File.Move(file.FullPath, destPath);

            var written = new FileInfo(destPath);
            operation.Moves.Add(new FileMove
            {
                SourcePath = file.FullPath,
                DestinationPath = destPath,
                WasCopied = copyMode,
                Size = written.Length,
                LastWriteTimeUtc = written.LastWriteTimeUtc
            });
            if (copyMode)
                JournalSource(file);
            duplicates?.Add(destPath, written.Length, file.FullPath);
        }
    }

    public async Task<UndoResult> UndoOperationAsync(OrganizeOperation operation, IProgress<int>? progress = null)
    {
        if (!CanUndoOperation(operation)) return new UndoResult(0, 0);

        await EnsureHistoryLoadedAsync();

        int restored = 0;
        int failed = 0;
        var pending = operation.Moves.Where(m => !m.IsUndone && IsUndoable(m)).ToList();

        await Task.Run(() =>
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var move = pending[i];
                try
                {
                    switch (UndoMove(move))
                    {
                        case UndoOutcome.Restored:
                            move.IsUndone = true;
                            restored++;
                            ForgetCopy(operation, move);
                            break;
                        case UndoOutcome.Gone:
                            // Nothing left to put back — don't keep the operation pending forever,
                            // which would shadow older operations for the Undo button.
                            move.IsUndone = true;
                            failed++;
                            ForgetCopy(operation, move);
                            break;
                        default:
                            failed++;
                            break;
                    }
                }
                catch
                {
                    failed++;
                }

                progress?.Report((i + 1) * 100 / pending.Count);
            }

            CleanEmptyFolders(pending.Where(m => m.IsUndone));
        });

        // Partially undone operations stay in history so the rest can be retried
        operation.IsUndone = operation.Moves.All(m => m.IsUndone || !IsUndoable(m));
        await HistoryService.SaveAsync(_history);
        await _journal.SaveAsync();

        return new UndoResult(restored, failed);
    }

    // Entries without a destination come from older versions that deleted duplicates outright
    private static bool IsUndoable(FileMove move) => !string.IsNullOrEmpty(move.DestinationPath);

    private enum UndoOutcome
    {
        Restored,
        /// <summary>Destination still exists but can't be touched safely (source reappeared, copy was modified) — retry later.</summary>
        Blocked,
        /// <summary>Destination no longer exists; there is nothing to restore.</summary>
        Gone
    }

    private static UndoOutcome UndoMove(FileMove move)
    {
        if (!File.Exists(move.DestinationPath))
            return UndoOutcome.Gone;

        if (move.WasCopied)
        {
            // Only delete the copy if it's still the file we wrote
            var info = new FileInfo(move.DestinationPath);
            if (info.Length != move.Size || info.LastWriteTimeUtc != move.LastWriteTimeUtc)
                return UndoOutcome.Blocked;

            File.Delete(move.DestinationPath);
            return UndoOutcome.Restored;
        }

        if (File.Exists(move.SourcePath))
            return UndoOutcome.Blocked;

        Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
        File.Move(move.DestinationPath, move.SourcePath);
        return UndoOutcome.Restored;
    }

    private void ForgetCopy(OrganizeOperation operation, FileMove move)
    {
        if (move.WasCopied)
            _journal.Remove(operation.SourceFolder, move.SourcePath);
    }

    private void TrimHistory() => HistoryService.Trim(_history);

    /// <summary>Groups files by the request's method or template; keys are relative paths using '/'.</summary>
    private static Dictionary<string, List<FileItem>> GroupFiles(List<FileItem> files, OrganizeRequest request)
    {
        var template = request.Template;

        // If template contains tokens, use template-based grouping
        if (!string.IsNullOrWhiteSpace(template) && template.Contains('#'))
            return GroupByTemplate(files, template, request.Categories);

        return request.Method switch
        {
            OrganizeMethod.ByType => GroupByType(files, request.Categories),
            OrganizeMethod.ByExtension => GroupByExtension(files),
            OrganizeMethod.ByDate => GroupByDate(files, request.DateFormat),
            OrganizeMethod.ByAlphabet => GroupByAlphabet(files),
            OrganizeMethod.BySize => GroupBySize(files),
            OrganizeMethod.ByDateTaken => GroupByDateTaken(files, request.DateFormat),
            OrganizeMethod.ByName => GroupByName(files),
            _ => GroupByType(files, request.Categories)
        };
    }

    private static Dictionary<string, List<FileItem>> GroupByType(List<FileItem> files, IReadOnlyList<FileCategory> categories)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var category = FileCategoryService.GetCategoryName(file.Extension, categories);
            if (!result.ContainsKey(category))
                result[category] = [];
            result[category].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByExtension(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var ext = string.IsNullOrEmpty(file.Extension) ? "No Extension" : file.Extension.TrimStart('.').ToUpperInvariant();
            if (!result.ContainsKey(ext))
                result[ext] = [];
            result[ext].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByDate(List<FileItem> files, string format)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = file.DateModified.ToString(format);
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByAlphabet(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var firstChar = string.IsNullOrEmpty(file.FileName) ? "#" : file.FileName[0].ToString().ToUpperInvariant();
            if (!char.IsLetter(firstChar[0]))
                firstChar = "#";
            if (!result.ContainsKey(firstChar))
                result[firstChar] = [];
            result[firstChar].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByName(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = NamePrefix.Get(file.FileName);
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupBySize(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = file.Size switch
            {
                < 1024 => "Tiny (< 1 KB)",
                < 1024 * 1024 => "Small (1 KB - 1 MB)",
                < 10 * 1024 * 1024 => "Medium (1 MB - 10 MB)",
                < 100 * 1024 * 1024 => "Large (10 MB - 100 MB)",
                < 1024L * 1024 * 1024 => "Very Large (100 MB - 1 GB)",
                _ => "Huge (> 1 GB)"
            };
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByDateTaken(List<FileItem> files, string format)
    {
        EnsureDateTakenLoaded(files);
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = file.DateTaken?.ToString(format) ?? "Unknown Date";
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    /// <summary>
    /// Loads EXIF DateTaken lazily only for files that support it and haven't been loaded yet.
    /// </summary>
    private static void EnsureDateTakenLoaded(List<FileItem> files)
    {
        foreach (var file in files)
        {
            if (!file.DateTakenLoaded && PhotoMetadataService.IsExifSupported(file.FullPath))
            {
                file.DateTaken = PhotoMetadataService.GetDateTaken(file.FullPath);
                file.DateTakenLoaded = true;
            }
        }
    }

    private static Dictionary<string, List<FileItem>> GroupByTemplate(
        List<FileItem> files, string template, IReadOnlyList<FileCategory> categories)
    {
        var result = new Dictionary<string, List<FileItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = ExpandTemplate(template, file, categories);
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    private static string ExpandTemplate(string template, FileItem file, IReadOnlyList<FileCategory> categories)
    {
        var result = template;
        result = result.Replace("#type", FileCategoryService.GetCategoryName(file.Extension, categories));
        result = result.Replace("#extension", file.Extension.TrimStart('.').ToUpperInvariant());
        result = result.Replace("#ext", file.Extension.TrimStart('.').ToUpperInvariant());
        result = result.Replace("#year", file.DateModified.Year.ToString());
        result = result.Replace("#month", file.DateModified.ToString("MM"));
        // Longer tokens first: "#date" is a prefix of "#datetaken" (same as #extension before #ext)
        if (result.Contains("#datetaken"))
        {
            if (!file.DateTakenLoaded && PhotoMetadataService.IsExifSupported(file.FullPath))
            {
                file.DateTaken = PhotoMetadataService.GetDateTaken(file.FullPath);
                file.DateTakenLoaded = true;
            }
            result = result.Replace("#datetaken", (file.DateTaken ?? file.DateModified).ToString("yyyy-MM"));
        }
        result = result.Replace("#date", file.DateModified.ToString("yyyy-MM-dd"));
        result = result.Replace("#prefix", NamePrefix.Get(file.FileName));
        result = result.Replace("#alpha", GetAlphaKey(file.FileName));
        result = result.Replace("#size", GetSizeKey(file.Size));

        // Trim whitespace and path separators
        return result.Trim().Trim('/', '\\');
    }

    private static string GetAlphaKey(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "#";
        var ch = fileName[0].ToString().ToUpperInvariant();
        return char.IsLetter(ch[0]) ? ch : "#";
    }

    private static string GetSizeKey(long size) => size switch
    {
        < 1024 => "Tiny",
        < 1024 * 1024 => "Small",
        < 10 * 1024 * 1024 => "Medium",
        < 100 * 1024 * 1024 => "Large",
        < 1024L * 1024 * 1024 => "Very Large",
        _ => "Huge"
    };

    private static string GetUniqueFileName(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;

        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        }
        return path;
    }

    private static void CleanEmptyFolders(IEnumerable<FileMove> undoneMoves)
    {
        var folders = undoneMoves
            .Select(m => Path.GetDirectoryName(m.DestinationPath))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private static bool PathsEqual(string a, string b) => PathKey.Equal(a, b);

    private static bool IsUnderFolder(string path, string folder) => PathKey.IsUnder(path, folder);

    /// <summary>Normalized folder path with a trailing separator, for prefix checks.</summary>
    private static string FolderPrefix(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Content-based duplicate lookup. Files are bucketed by size and only hashed (SHA-256)
    /// when another file of exactly the same size exists, so most files are never read.
    /// </summary>
    private sealed class DuplicateIndex
    {
        private readonly Dictionary<long, List<string>> _pathsBySize = [];
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scannedFolders = new(StringComparer.OrdinalIgnoreCase);

        public void AddExistingFiles(string folder)
        {
            if (!_scannedFolders.Add(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                try { Add(path, new FileInfo(path).Length); }
                catch { /* skip unreadable */ }
            }
        }

        /// <param name="hashSourcePath">Where the content was read from if it was already hashed under another path (before a move).</param>
        public void Add(string path, long size, string? hashSourcePath = null)
        {
            if (!_known.Add(path)) return;
            if (hashSourcePath != null && _hashes.TryGetValue(hashSourcePath, out var hash))
                _hashes[path] = hash;
            if (!_pathsBySize.TryGetValue(size, out var list))
                _pathsBySize[size] = list = [];
            list.Add(path);
        }

        /// <summary>True if a different file with identical content is already indexed.</summary>
        public bool IsDuplicate(string path, long size)
        {
            if (!_pathsBySize.TryGetValue(size, out var candidates)) return false;

            string? hash = null;
            foreach (var other in candidates)
            {
                if (string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) continue;

                hash ??= GetHash(path);
                if (hash == null) return false;
                if (GetHash(other) == hash) return true;
            }
            return false;
        }

        private string? GetHash(string path)
        {
            if (_hashes.TryGetValue(path, out var hash)) return hash;
            try
            {
                using var stream = File.OpenRead(path);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch
            {
                hash = null;
            }
            _hashes[path] = hash;
            return hash;
        }
    }
}

public readonly record struct UndoResult(int Restored, int Failed);
