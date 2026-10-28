using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class HistoryTests
{
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    public HistoryTests()
    {
        TestData.RedirectAppData();
        JsonFileStore.DataDir = TestData.NewTempDir("history");
    }

    private Task<OrganizeOperation> MoveOneFileAsync(FileOrganizerService svc, string dir, string name)
    {
        var path = TestData.WriteFile(dir, name);
        var request = OrganizeRequest.FromProfile(FolderProfile.CreateDefault(dir), _categories);
        return svc.OrganizeAsync([TestData.ItemFromDisk(path)], request, RunContext.NewManual());
    }

    [Fact]
    public async Task Undo_WhenEveryDestinationIsGone_MarksOperationUndoneAndUnblocksOlderOnes()
    {
        var svc = new FileOrganizerService();
        var dir = TestData.NewTempDir();
        var older = await MoveOneFileAsync(svc, dir, "a.txt");
        var newer = await MoveOneFileAsync(svc, dir, "b.txt");

        File.Delete(newer.Moves.Single().DestinationPath); // user removed it outside the app

        var result = await svc.UndoOperationAsync(newer);

        Assert.Equal(1, result.Failed);
        Assert.True(newer.IsUndone);
        Assert.Equal([older], svc.LastUndoableRun);
    }

    [Fact]
    public async Task Organize_WhileHistoryIsStillLoading_IsNotLostWhenLoadCompletes()
    {
        // A large history.json makes LoadHistoryAsync slow enough for an organize to slip in between.
        var big = Enumerable.Range(0, 40).Select(i => new OrganizeOperation
        {
            Description = $"op {i}",
            Moves = Enumerable.Range(0, 2000).Select(j => new FileMove
            {
                SourcePath = $@"C:\src\{i}\{j}.txt",
                DestinationPath = $@"C:\dst\{i}\{j}.txt"
            }).ToList()
        }).ToList();
        JsonFileStore.Save("history.json", big);

        var svc = new FileOrganizerService();
        var dir = TestData.NewTempDir();

        var load = svc.LoadHistoryAsync();
        var fresh = await MoveOneFileAsync(svc, dir, "a.txt");
        await load;

        Assert.Contains(fresh, svc.History);
        Assert.Same(fresh, svc.History[^1]);
    }
}
