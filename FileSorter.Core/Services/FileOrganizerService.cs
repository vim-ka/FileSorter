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
    private List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    public IReadOnlyList<OrganizeOperation> History => _history;

    public async Task LoadHistoryAsync()
    {
        var loaded = await HistoryService.LoadAsync();
        _history.Clear();
        _history.AddRange(loaded);
    }

    public async Task ClearHistoryAsync()
    {
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

    public OrganizeOperation? LastUndoableOperation => _history.LastOrDefault(CanUndoOperation);

    public void SetCategories(List<FileCategory> categories)
    {
        _categories = categories;
    }

    /// <summary>
    /// Scans folder in background. Calls onBatch on UI thread for each batch, awaiting completion
    /// before continuing (provides backpressure so UI doesn't get flooded).
    /// </summary>
    public async Task ScanFolderAsync(string folderPath, bool includeSubfolders,
        Func<List<FileItem>, Task> onBatch, int batchSize, CancellationToken ct)
    {
        if (!Directory.Exists(folderPath)) return;

        var option = includeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        await Task.Run(async () =>
        {
            var batch = new List<FileItem>(batchSize);
            var duplicatesFolder = Path.Combine(folderPath, DuplicatesFolderName);

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", option))
            {
                ct.ThrowIfCancellationRequested();
                if (IsUnderFolder(filePath, duplicatesFolder)) continue;

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

    public ObservableCollection<PreviewItem> GeneratePreview(
        List<FileItem> files, OrganizeMethod method, List<FileCategory> activeCategories,
        string? template = null, string? dateFormat = null)
    {
        var preview = new ObservableCollection<PreviewItem>();
        var grouped = GroupFiles(files, method, activeCategories, template, dateFormat);

        foreach (var group in grouped.OrderBy(g => g.Key))
        {
            var pathParts = group.Key.Split('/', '\\');
            var currentChildren = preview;

            for (int i = 0; i < pathParts.Length; i++)
            {
                var existing = currentChildren.FirstOrDefault(p => p.Name == pathParts[i] && p.IsFolder);
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

                if (i == pathParts.Length - 1)
                {
                    existing.FileCount += group.Value.Count;
                    foreach (var file in group.Value.OrderBy(f => f.FileName))
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

    public async Task<OrganizeOperation> OrganizeAsync(
        List<FileItem> files,
        string sourceFolder,
        string? destinationFolder,
        OrganizeMethod method,
        bool copyMode,
        List<FileCategory> activeCategories,
        IProgress<int>? progress = null,
        string? template = null,
        string? dateFormat = null,
        bool skipDuplicates = false)
    {
        var targetFolder = destinationFolder ?? sourceFolder;
        var duplicatesFolder = Path.Combine(targetFolder, DuplicatesFolderName);
        var grouped = GroupFiles(files, method, activeCategories, template, dateFormat);
        var operation = new OrganizeOperation();
        var duplicates = skipDuplicates ? new DuplicateIndex() : null;

        int processed = 0;
        int total = grouped.Sum(g => g.Value.Count);

        try
        {
            await Task.Run(() =>
            {
                foreach (var group in grouped)
                {
                    var normalizedKey = group.Key.Replace('/', Path.DirectorySeparatorChar);
                    var folderPath = Path.Combine(targetFolder, normalizedKey);

                    try
                    {
                        Directory.CreateDirectory(folderPath);
                        duplicates?.AddExistingFiles(folderPath);
                    }
                    catch
                    {
                        // Can't create or read the target folder — every file of the group fails
                        operation.FailedFiles += group.Value.Count;
                        processed += group.Value.Count;
                        progress?.Report(processed * 100 / Math.Max(total, 1));
                        continue;
                    }

                    foreach (var file in group.Value)
                    {
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
        }
        finally
        {
            // Record whatever was done, even if something unexpected aborted the loop,
            // so it can still be undone.
            var verb = copyMode ? "Copied" : "Moved";
            var by = template != null && template.Contains('#') ? "template" : method.ToString();
            operation.Description = $"{verb} {operation.Moves.Count(m => !m.WasDuplicate)} files by {by}";

            if (operation.Moves.Count > 0)
            {
                _history.Add(operation);
                TrimHistory();
                await HistoryService.SaveAsync(_history);
            }
        }

        return operation;

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
                    return; // nothing to do — the original stays untouched

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
            duplicates?.Add(destPath, written.Length, file.FullPath);
        }
    }

    public async Task<UndoResult> UndoOperationAsync(OrganizeOperation operation, IProgress<int>? progress = null)
    {
        if (!CanUndoOperation(operation)) return new UndoResult(0, 0);

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
                    if (UndoMove(move))
                    {
                        move.IsUndone = true;
                        restored++;
                    }
                    else
                    {
                        failed++;
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

        return new UndoResult(restored, failed);
    }

    // Entries without a destination come from older versions that deleted duplicates outright
    private static bool IsUndoable(FileMove move) => !string.IsNullOrEmpty(move.DestinationPath);

    private static bool UndoMove(FileMove move)
    {
        if (!File.Exists(move.DestinationPath))
            return false;

        if (move.WasCopied)
        {
            // Only delete the copy if it's still the file we wrote
            var info = new FileInfo(move.DestinationPath);
            if (info.Length != move.Size || info.LastWriteTimeUtc != move.LastWriteTimeUtc)
                return false;

            File.Delete(move.DestinationPath);
            return true;
        }

        if (File.Exists(move.SourcePath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
        File.Move(move.DestinationPath, move.SourcePath);
        return true;
    }

    private void TrimHistory()
    {
        var excess = _history.Count - HistoryService.MaxOperations;
        if (excess > 0)
            _history.RemoveRange(0, excess);
    }

    private Dictionary<string, List<FileItem>> GroupFiles(
        List<FileItem> files, OrganizeMethod method, List<FileCategory> activeCategories,
        string? template = null, string? dateFormat = null)
    {
        var activeCategoryNames = activeCategories
            .Where(c => c.IsSelected)
            .Select(c => c.Name)
            .ToHashSet();

        var filteredFiles = files.Where(f =>
        {
            var cat = FileCategoryService.GetCategoryName(f.Extension, activeCategories);
            return activeCategoryNames.Contains(cat);
        }).ToList();

        // If template contains tokens, use template-based grouping
        if (!string.IsNullOrWhiteSpace(template) && template.Contains('#'))
            return GroupByTemplate(filteredFiles, template, activeCategories);

        return method switch
        {
            OrganizeMethod.ByType => GroupByType(filteredFiles),
            OrganizeMethod.ByExtension => GroupByExtension(filteredFiles),
            OrganizeMethod.ByDate => GroupByDate(filteredFiles, dateFormat ?? "yyyy-MM"),
            OrganizeMethod.ByAlphabet => GroupByAlphabet(filteredFiles),
            OrganizeMethod.BySize => GroupBySize(filteredFiles),
            OrganizeMethod.ByDateTaken => GroupByDateTaken(filteredFiles, dateFormat ?? "yyyy-MM"),
            _ => GroupByType(filteredFiles)
        };
    }

    private Dictionary<string, List<FileItem>> GroupByType(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>();
        foreach (var file in files)
        {
            var category = FileCategoryService.GetCategoryName(file.Extension, _categories);
            if (!result.ContainsKey(category))
                result[category] = [];
            result[category].Add(file);
        }
        return result;
    }

    private static Dictionary<string, List<FileItem>> GroupByExtension(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>();
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
        var result = new Dictionary<string, List<FileItem>>();
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
        var result = new Dictionary<string, List<FileItem>>();
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

    private static Dictionary<string, List<FileItem>> GroupBySize(List<FileItem> files)
    {
        var result = new Dictionary<string, List<FileItem>>();
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

    private Dictionary<string, List<FileItem>> GroupByDateTaken(List<FileItem> files, string format)
    {
        EnsureDateTakenLoaded(files);
        var result = new Dictionary<string, List<FileItem>>();
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

    private Dictionary<string, List<FileItem>> GroupByTemplate(
        List<FileItem> files, string template, List<FileCategory> categories)
    {
        var result = new Dictionary<string, List<FileItem>>();
        foreach (var file in files)
        {
            var key = ExpandTemplate(template, file, categories);
            if (!result.ContainsKey(key))
                result[key] = [];
            result[key].Add(file);
        }
        return result;
    }

    private static string ExpandTemplate(string template, FileItem file, List<FileCategory> categories)
    {
        var result = template;
        result = result.Replace("#type", FileCategoryService.GetCategoryName(file.Extension, categories));
        result = result.Replace("#extension", file.Extension.TrimStart('.').ToUpperInvariant());
        result = result.Replace("#ext", file.Extension.TrimStart('.').ToUpperInvariant());
        result = result.Replace("#year", file.DateModified.Year.ToString());
        result = result.Replace("#month", file.DateModified.ToString("MM"));
        result = result.Replace("#date", file.DateModified.ToString("yyyy-MM-dd"));
        if (result.Contains("#datetaken"))
        {
            if (!file.DateTakenLoaded && PhotoMetadataService.IsExifSupported(file.FullPath))
            {
                file.DateTaken = PhotoMetadataService.GetDateTaken(file.FullPath);
                file.DateTakenLoaded = true;
            }
        }
        result = result.Replace("#datetaken", (file.DateTaken ?? file.DateModified).ToString("yyyy-MM"));
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

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderFolder(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

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
