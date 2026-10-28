using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class OrganizeRulesTests : TempDataDirTest
{
    private static readonly List<FileCategory> Categories = FileCategoryService.GetDefaultCategories();

    private readonly string _source;

    public OrganizeRulesTests()
    {
        _source = Path.Combine(TempDir, "src");
        Directory.CreateDirectory(_source);
    }

    private OrganizeRequest Request(params RoutingRule[] rules)
    {
        var profile = FolderProfile.CreateDefault(_source);
        profile.Rules.AddRange(rules);
        return OrganizeRequest.FromProfile(profile, Categories);
    }

    [Fact]
    public async Task Organize_RuleFiles_UndoRestoresThem()
    {
        var clip = TestData.WriteFile(_source, "clip.mp4");
        var notes = TestData.WriteFile(_source, "notes.txt");
        var fullPathTarget = Path.Combine(TempDir, "Video");
        var request = Request(
            PlannerTests.Rule(fullPathTarget, RuleField.Extension, RuleOperator.Is, "mp4"),
            PlannerTests.Rule("Texts", RuleField.Extension, RuleOperator.Is, "txt"));
        var svc = new FileOrganizerService();

        var op = await svc.OrganizeAsync([TestData.ItemFromDisk(clip), TestData.ItemFromDisk(notes)], request);

        Assert.True(File.Exists(Path.Combine(fullPathTarget, "clip.mp4")));
        Assert.True(File.Exists(Path.Combine(_source, "Texts", "notes.txt")));
        Assert.False(File.Exists(clip));
        Assert.Equal(0, op.FailedFiles);

        var undo = await svc.UndoOperationAsync(op);

        Assert.Equal(2, undo.Restored);
        Assert.True(File.Exists(clip));
        Assert.True(File.Exists(notes));
        Assert.True(op.IsUndone);
    }

    [Fact]
    public async Task Organize_RuleToUnavailableDrive_CountsFailedAndContinues()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).First(c => !used.Contains(c));
        var clip = TestData.WriteFile(_source, "clip.mp4");
        var notes = TestData.WriteFile(_source, "notes.txt");
        var request = Request(PlannerTests.Rule($@"{letter}:\NoSuchDrive\X", RuleField.Extension, RuleOperator.Is, "mp4"));
        var svc = new FileOrganizerService();

        var op = await svc.OrganizeAsync([TestData.ItemFromDisk(clip), TestData.ItemFromDisk(notes)], request);

        Assert.Equal(1, op.FailedFiles);
        Assert.True(File.Exists(clip));
        Assert.True(File.Exists(Path.Combine(_source, "Documents", "notes.txt")));
        Assert.Contains(op, svc.History);
    }

    [Fact]
    public async Task Organize_RuleFileAlreadyInPlace_IsLeftAlone()
    {
        var moviesDir = Path.Combine(_source, "Movies");
        Directory.CreateDirectory(moviesDir);
        var clip = TestData.WriteFile(moviesDir, "clip.mp4");
        var request = Request(PlannerTests.Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4"));
        var svc = new FileOrganizerService();

        var op = await svc.OrganizeAsync([TestData.ItemFromDisk(clip)], request);

        Assert.Empty(op.Moves);
        Assert.True(File.Exists(clip));
    }
}
