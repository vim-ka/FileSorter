using System.Collections.ObjectModel;
using System.IO;
using FileSorter.Models;

namespace FileSorter.Services;

public class FileOrganizerService
{
    private readonly Stack<OrganizeOperation> _undoStack = new();
    private readonly List<OrganizeOperation> _history = [];
    private List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    public bool CanUndo => _undoStack.Count > 0;
    public IReadOnlyList<OrganizeOperation> History => _history;

    public void ClearHistory()
    {
        _history.Clear();
        _undoStack.Clear();
    }

    public void SetCategories(List<FileCategory> categories)
    {
        _categories = categories;
    }

    public List<FileItem> ScanFolder(string folderPath, bool includeSubfolders)
    {
        if (!Directory.Exists(folderPath))
            return [];

        var option = includeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var files = new List<FileItem>();
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", option))
        {
            try
            {
                var info = new FileInfo(filePath);
                files.Add(new FileItem
                {
                    FullPath = filePath,
                    Size = info.Length,
                    DateModified = info.LastWriteTime,
                    DateCreated = info.CreationTime
                });
            }
            catch
            {
                // Skip files we can't access
            }
        }
        return files;
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

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", option))
            {
                ct.ThrowIfCancellationRequested();

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
                            Icon = "\uE8A5"
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
        string? dateFormat = null)
    {
        var targetFolder = destinationFolder ?? sourceFolder;
        var grouped = GroupFiles(files, method, activeCategories, template, dateFormat);
        var operation = new OrganizeOperation
        {
            Description = $"Organized {files.Count} files by {method}"
        };

        int processed = 0;
        int total = files.Count;

        await Task.Run(() =>
        {
            foreach (var group in grouped)
            {
                var normalizedKey = group.Key.Replace('/', Path.DirectorySeparatorChar);
                var folderPath = Path.Combine(targetFolder, normalizedKey);
                Directory.CreateDirectory(folderPath);

                foreach (var file in group.Value)
                {
                    var destPath = Path.Combine(folderPath, file.FileName);
                    destPath = GetUniqueFileName(destPath);

                    try
                    {
                        if (copyMode)
                        {
                            File.Copy(file.FullPath, destPath, false);
                        }
                        else
                        {
                            File.Move(file.FullPath, destPath);
                        }

                        operation.Moves.Add(new FileMove
                        {
                            SourcePath = file.FullPath,
                            DestinationPath = destPath,
                            WasCopied = copyMode
                        });
                    }
                    catch
                    {
                        // Skip files that fail
                    }

                    processed++;
                    progress?.Report((int)((double)processed / total * 100));
                }
            }
        });

        if (operation.Moves.Count > 0)
        {
            _undoStack.Push(operation);
            _history.Add(operation);
        }

        return operation;
    }

    public async Task<int> UndoLastOperationAsync(IProgress<int>? progress = null)
    {
        if (!CanUndo) return 0;

        var operation = _undoStack.Pop();
        int undone = 0;
        int total = operation.Moves.Count;

        await Task.Run(() =>
        {
            foreach (var move in operation.Moves)
            {
                try
                {
                    if (move.WasCopied)
                    {
                        if (File.Exists(move.DestinationPath))
                            File.Delete(move.DestinationPath);
                    }
                    else
                    {
                        if (File.Exists(move.DestinationPath) && !File.Exists(move.SourcePath))
                            File.Move(move.DestinationPath, move.SourcePath);
                    }
                    undone++;
                }
                catch
                {
                    // Skip files that fail to undo
                }

                progress?.Report((int)((double)undone / total * 100));
            }

            // Clean up empty folders
            CleanEmptyFolders(operation);
        });

        _history.Add(new OrganizeOperation
        {
            Description = $"Undone: {operation.Description} ({undone} files restored)",
            Timestamp = DateTime.Now
        });

        return undone;
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

    private static void CleanEmptyFolders(OrganizeOperation operation)
    {
        var folders = operation.Moves
            .Select(m => Path.GetDirectoryName(m.DestinationPath)!)
            .Distinct();

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
}
