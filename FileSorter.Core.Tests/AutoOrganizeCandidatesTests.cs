using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class AutoOrganizeCandidatesTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stability = TimeSpan.FromSeconds(30);

    private static FileItem File(string name, TimeSpan age, long size = 10) =>
        TestData.Item(Path.Combine(@"C:\Watched", name), (Now - age).ToLocalTime(), size);

    private static FileItem Stable(string name) => File(name, TimeSpan.FromMinutes(5));

    private static CandidateResult Select(IReadOnlyList<FileItem> files, Func<FileItem, bool>? isProcessed = null) =>
        AutoOrganizeCandidates.Select(files, Now, Stability, isProcessed ?? (_ => false));

    /// <summary>Select, plan with <paramref name="profile"/> (default: by type), then drop locked files.</summary>
    private static CandidateResult SelectAndDropLocked(IReadOnlyList<FileItem> files, Func<string, bool> isLocked,
        FolderProfile? profile = null)
    {
        var selection = Select(files);
        var request = OrganizeRequest.FromProfile(profile ?? FolderProfile.CreateDefault(@"C:\Watched"),
            FileCategoryService.GetDefaultCategories());
        return AutoOrganizeCandidates.DropLocked(selection, FileOrganizerService.Plan(selection.Ready, request), isLocked);
    }

    [Fact]
    public void PartialExtensions_AreTheSixKnownOnes()
    {
        Assert.Equal(
            [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload"],
            AutoOrganizeCandidates.PartialExtensions);
    }

    [Theory]
    [InlineData(".crdownload")]
    [InlineData(".part")]
    [InlineData(".partial")]
    [InlineData(".tmp")]
    [InlineData(".download")]
    [InlineData(".opdownload")]
    [InlineData(".PART")]
    public void Candidates_SkipsPartialExtensions(string extension)
    {
        var result = Select([Stable("movie" + extension), Stable("notes.txt")]);

        Assert.Equal(["notes.txt"], result.Ready.Select(f => f.FileName));
    }

    [Fact]
    public void Candidates_SkipFileWithPartialSibling()
    {
        var result = Select([Stable("report.pdf"), File("report.pdf.part", TimeSpan.FromSeconds(1))]);

        Assert.Empty(result.Ready);
        Assert.True(result.HasPending);
    }

    [Fact]
    public void Candidates_StalePartialSibling_SkippedWithoutRetry()
    {
        // An abandoned download must not be broken, but it also must not make the engine rescan forever
        var result = Select([Stable("report.pdf"), Stable("report.pdf.crdownload")]);

        Assert.Empty(result.Ready);
        Assert.False(result.HasPending);
    }

    [Fact]
    public void Candidates_ModifiedWithinStability_Pending()
    {
        var result = Select([File("fresh.txt", TimeSpan.FromSeconds(5))]);

        Assert.Empty(result.Ready);
        Assert.True(result.HasPending);
    }

    [Fact]
    public void Candidates_StableFile_Ready()
    {
        var result = Select([File("old.txt", TimeSpan.FromSeconds(31))]);

        Assert.Equal(["old.txt"], result.Ready.Select(f => f.FileName));
        Assert.False(result.HasPending);
    }

    [Fact]
    public void Candidates_Locked_Pending()
    {
        var result = SelectAndDropLocked([Stable("open.docx"), Stable("free.docx")], p => p.EndsWith("open.docx"));

        Assert.Equal(["free.docx"], result.Ready.Select(f => f.FileName));
        Assert.True(result.HasPending);
    }

    [Fact]
    public void DropLocked_OnlyFilesThePlanMoves_AreOpened()
    {
        var profile = FolderProfile.CreateDefault(@"C:\Watched");
        profile.Unmatched = UnmatchedFilesMode.LeaveInPlace;
        profile.Rules.Add(new RoutingRule
        {
            Destination = "Movies",
            Conditions = [new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "mp4" }]
        });
        profile.Rules.Add(new RoutingRule
        {
            Destination = ".", // already where it belongs
            Conditions = [new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "txt" }]
        });
        var opened = new List<string>();

        var result = SelectAndDropLocked([Stable("clip.mp4"), Stable("notes.txt"), Stable("other.docx")],
            p => { opened.Add(Path.GetFileName(p)); return false; }, profile);

        Assert.Equal(["clip.mp4"], opened);
        Assert.Equal(["clip.mp4"], result.Ready.Select(f => f.FileName));
        Assert.False(result.HasPending);
    }

    [Fact]
    public void Candidates_InJournal_Skipped()
    {
        var result = Select([Stable("copied.txt"), File("copied-fresh.txt", TimeSpan.Zero)], isProcessed: _ => true);

        Assert.Empty(result.Ready);
        Assert.False(result.HasPending);
    }

    [Fact]
    public void IsLocked_FileOpenedExclusively_True()
    {
        var dir = TestData.NewTempDir();
        var path = TestData.WriteFile(dir, "a.txt");

        Assert.False(AutoOrganizeCandidates.IsLocked(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(AutoOrganizeCandidates.IsLocked(path));
        Assert.False(AutoOrganizeCandidates.IsLocked(Path.Combine(dir, "missing.txt")));
    }
}
