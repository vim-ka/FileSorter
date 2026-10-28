using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class CopyJournalTests : TempDataDirTest
{
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    private OrganizeRequest CopyRequest(string dir, bool skipDuplicates = false)
    {
        var profile = FolderProfile.CreateDefault(dir);
        profile.CopyMode = true;
        profile.SkipDuplicates = skipDuplicates;
        return OrganizeRequest.FromProfile(profile, _categories);
    }

    [Fact]
    public async Task ManualCopy_AddsSourceToJournal()
    {
        var dir = TestData.NewTempDir();
        var item = TestData.ItemFromDisk(TestData.WriteFile(dir, "a.txt"));
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);

        await svc.OrganizeAsync([item], CopyRequest(dir), RunContext.NewManual());

        Assert.True(journal.Contains(dir, item));
    }

    [Fact]
    public async Task MoveMode_DoesNotTouchJournal()
    {
        var dir = TestData.NewTempDir();
        var item = TestData.ItemFromDisk(TestData.WriteFile(dir, "a.txt"));
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);
        var request = OrganizeRequest.FromProfile(FolderProfile.CreateDefault(dir), _categories);

        await svc.OrganizeAsync([item], request, RunContext.NewManual());

        Assert.False(journal.Contains(dir, item));
    }

    [Fact]
    public async Task CopySkippedDuplicate_AddsToJournal()
    {
        var dir = TestData.NewTempDir();
        var first = TestData.ItemFromDisk(TestData.WriteFile(dir, "a.txt", "same"));
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);
        await svc.OrganizeAsync([first], CopyRequest(dir, skipDuplicates: true), RunContext.NewManual());

        var second = TestData.ItemFromDisk(TestData.WriteFile(dir, "b.txt", "same"));
        var op = await svc.OrganizeAsync([second], CopyRequest(dir, skipDuplicates: true), RunContext.NewManual());

        Assert.Equal(1, op.SkippedDuplicates);
        Assert.True(journal.Contains(dir, second));
    }

    [Fact]
    public async Task Contains_ChangedFile_False()
    {
        var dir = TestData.NewTempDir();
        var path = TestData.WriteFile(dir, "a.txt");
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);
        await svc.OrganizeAsync([TestData.ItemFromDisk(path)], CopyRequest(dir), RunContext.NewManual());

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
        Assert.False(journal.Contains(dir, TestData.ItemFromDisk(path)));

        File.WriteAllText(path, "different and longer content");
        Assert.False(journal.Contains(dir, TestData.ItemFromDisk(path)));
    }

    [Fact]
    public async Task UndoCopy_RemovesFromJournal()
    {
        var dir = TestData.NewTempDir();
        var item = TestData.ItemFromDisk(TestData.WriteFile(dir, "a.txt"));
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);
        var op = await svc.OrganizeAsync([item], CopyRequest(dir), RunContext.NewManual());

        await svc.UndoOperationAsync(op);

        Assert.False(journal.Contains(dir, item));
    }

    [Fact]
    public void Prune_RemovesMissingFiles()
    {
        var dir = TestData.NewTempDir();
        var keep = TestData.ItemFromDisk(TestData.WriteFile(dir, "keep.txt"));
        var gone = TestData.ItemFromDisk(TestData.WriteFile(dir, "gone.txt"));
        var other = TestData.NewTempDir();
        var otherItem = TestData.ItemFromDisk(TestData.WriteFile(other, "x.txt"));
        var journal = new CopyJournal();
        foreach (var (folder, f) in new[] { (dir, keep), (dir, gone), (other, otherItem) })
            journal.Add(folder, f.FullPath, f.Size, f.DateModified.ToUniversalTime());

        journal.Prune(dir, [keep.FullPath.ToUpperInvariant()]);

        Assert.True(journal.Contains(dir, keep));
        Assert.False(journal.Contains(dir, gone));
        Assert.True(journal.Contains(other, otherItem));
    }

    [Fact]
    public async Task Journal_SurvivesReload()
    {
        var dir = TestData.NewTempDir();
        var item = TestData.ItemFromDisk(TestData.WriteFile(dir, "a.txt"));
        var journal = new CopyJournal();
        journal.Add(dir, item.FullPath, item.Size, item.DateModified.ToUniversalTime());
        await journal.SaveAsync();

        var reloaded = await CopyJournal.LoadAsync();

        Assert.True(reloaded.Contains(dir + Path.DirectorySeparatorChar, item));
    }
}
