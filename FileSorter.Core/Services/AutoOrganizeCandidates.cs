using FileSorter.Models;

namespace FileSorter.Services;

/// <param name="Ready">Files that can be organized now.</param>
/// <param name="HasPending">Some files were skipped only for now (still changing, open in another
/// program, or next to a download in progress) — worth checking again after the stability window.</param>
public sealed record CandidateResult(IReadOnlyList<FileItem> Ready, bool HasPending);

/// <summary>
/// Decides which root files auto-organize may take right now: <see cref="Select"/> (pure), then the
/// run plans those and <see cref="DropLocked"/> opens only the files the plan would move or copy.
/// </summary>
public static class AutoOrganizeCandidates
{
    /// <summary>Extensions of downloads in progress (Chrome/Edge, Firefox, Safari, Opera, generic).</summary>
    public static readonly string[] PartialExtensions =
        [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload"];

    /// <param name="rootFiles">Files in the folder root.</param>
    /// <param name="isProcessed">Already handled and unchanged (copy journal, files the engine placed here) — skipped silently.</param>
    public static CandidateResult Select(IReadOnlyList<FileItem> rootFiles, DateTime nowUtc, TimeSpan stability,
        Func<FileItem, bool> isProcessed)
    {
        // Downloads in progress, by full path, with whether they are still being written
        var partials = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in rootFiles)
            if (IsPartial(file))
                partials[file.FullPath] = IsChanging(file, nowUtc, stability);

        var ready = new List<FileItem>();
        var hasPending = false;

        foreach (var file in rootFiles)
        {
            if (IsPartial(file) || isProcessed(file)) continue;

            // "report.pdf" next to "report.pdf.part": the browser's placeholder for a download in
            // progress — taking it would break the download. Retry only while the download is active.
            var sibling = PartialExtensions
                .Select(ext => file.FullPath + ext)
                .FirstOrDefault(partials.ContainsKey);
            if (sibling != null)
            {
                hasPending |= partials[sibling];
                continue;
            }

            if (IsChanging(file, nowUtc, stability))
            {
                hasPending = true;
                continue;
            }

            ready.Add(file);
        }

        return new CandidateResult(ready, hasPending);
    }

    /// <summary>
    /// Keeps the planned files that would actually move or copy (not those already where they
    /// belong) and are not open in another program. Locked files are pending: retried next time.
    /// Files the plan leaves alone (not in it) are never opened.
    /// </summary>
    /// <param name="selection">Result of <see cref="Select"/>.</param>
    /// <param name="plan">Plan of <c>selection.Ready</c>.</param>
    /// <param name="isLocked">Open in another program (see <see cref="IsLocked"/>).</param>
    public static CandidateResult DropLocked(CandidateResult selection, IReadOnlyList<PlannedGroup> plan,
        Func<string, bool> isLocked)
    {
        var ready = new List<FileItem>();
        var hasPending = selection.HasPending;
        foreach (var group in plan)
        {
            foreach (var file in group.Files)
            {
                if (PathKey.Equal(Path.Combine(group.FolderPath, file.FileName), file.FullPath)) continue;
                if (isLocked(file.FullPath))
                {
                    hasPending = true;
                    continue;
                }
                ready.Add(file);
            }
        }
        return new CandidateResult(ready, hasPending);
    }

    /// <summary>True when the file can't be opened for exclusive reading (another program has it open).</summary>
    public static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }      // gone — nothing to wait for
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }                  // sharing violation
        catch (UnauthorizedAccessException) { return false; } // not a lock; organizing will report it as failed
    }

    private static bool IsPartial(FileItem file) =>
        PartialExtensions.Contains(Path.GetExtension(file.FullPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>Modified within the stability window (a timestamp slightly in the future counts too).</summary>
    private static bool IsChanging(FileItem file, DateTime nowUtc, TimeSpan stability)
    {
        var age = nowUtc - file.DateModified.ToUniversalTime();
        return age < stability && age > -stability;
    }
}
