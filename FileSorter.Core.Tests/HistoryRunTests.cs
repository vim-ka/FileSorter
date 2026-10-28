using FileSorter.Models;
using FileSorter.Services;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class HistoryRunTests : TempDataDirTest
{
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    private Task<OrganizeOperation> MoveOneAsync(FileOrganizerService svc, string dir, string name, RunContext run)
    {
        var path = TestData.WriteFile(dir, name);
        var request = OrganizeRequest.FromProfile(FolderProfile.CreateDefault(dir), _categories);
        return svc.OrganizeAsync([TestData.ItemFromDisk(path)], request, run);
    }

    [Fact]
    public void Trim_KeepsLimitsSeparately()
    {
        // 60 manual and 210 auto operations, interleaved chronologically
        var list = new List<OrganizeOperation>();
        int m = 0, a = 0;
        for (int i = 0; i < 270; i++)
        {
            bool auto = i % 9 != 0 && a < 210 || m >= 60;
            if (auto) list.Add(new OrganizeOperation { Description = $"auto{a++}", IsAuto = true });
            else list.Add(new OrganizeOperation { Description = $"manual{m++}" });
        }

        HistoryService.Trim(list);

        Assert.Equal(50, list.Count(o => !o.IsAuto));
        Assert.Equal(200, list.Count(o => o.IsAuto));
        Assert.DoesNotContain(list, o => o.Description == "manual9");
        Assert.Contains(list, o => o.Description == "manual10");
        Assert.DoesNotContain(list, o => o.Description == "auto9");
        Assert.Contains(list, o => o.Description == "auto10");
        Assert.Contains(list, o => o.Description == "manual59");
        Assert.Contains(list, o => o.Description == "auto209");
    }

    [Fact]
    public void Trim_PreservesChronologicalOrder()
    {
        var list = new List<OrganizeOperation>();
        for (int i = 0; i < 300; i++)
            list.Add(new OrganizeOperation { Description = i.ToString(), IsAuto = i % 2 == 0 });

        HistoryService.Trim(list);

        var ids = list.Select(o => int.Parse(o.Description)).ToList();
        Assert.Equal(ids.OrderBy(x => x).ToList(), ids);
    }

    [Fact]
    public async Task LastUndoableRun_ReturnsAllOperationsOfLatestRun()
    {
        var svc = new FileOrganizerService();
        await MoveOneAsync(svc, TestData.NewTempDir(), "a.txt", RunContext.NewManual());
        var run = RunContext.NewManual();
        var x = await MoveOneAsync(svc, TestData.NewTempDir(), "b.txt", run);
        var y = await MoveOneAsync(svc, TestData.NewTempDir(), "c.txt", run);

        Assert.Equal([x, y], svc.LastUndoableRun);
    }

    [Fact]
    public async Task LastUndoableRun_LegacyEmptyRunId_IsSingleOperation()
    {
        var svc = new FileOrganizerService();
        var first = await MoveOneAsync(svc, TestData.NewTempDir(), "a.txt", RunContext.NewManual());
        var second = await MoveOneAsync(svc, TestData.NewTempDir(), "b.txt", RunContext.NewManual());
        first.RunId = Guid.Empty;
        second.RunId = Guid.Empty;

        Assert.Equal([second], svc.LastUndoableRun);
    }

    [Fact]
    public async Task LastUndoableRun_NothingToUndo_IsEmpty()
    {
        var svc = new FileOrganizerService();
        Assert.Empty(svc.LastUndoableRun);
        var op = await MoveOneAsync(svc, TestData.NewTempDir(), "a.txt", RunContext.NewManual());
        await svc.UndoOperationAsync(op);
        Assert.Empty(svc.LastUndoableRun);
    }

    [Fact]
    public async Task Organize_RecordsRunSourceFolderAndAutoDescription()
    {
        var svc = new FileOrganizerService();
        var dir = TestData.NewTempDir("Downloads");
        var run = RunContext.NewAuto();
        var op = await MoveOneAsync(svc, dir, "a.txt", run);

        Assert.Equal(run.RunId, op.RunId);
        Assert.True(op.IsAuto);
        Assert.Equal(dir, op.SourceFolder);
        Assert.Equal($"Auto · {Path.GetFileName(dir)}: Moved 1 files by ByType", op.Description);

        var manual = await MoveOneAsync(svc, dir, "b.txt", RunContext.NewManual());
        Assert.False(manual.IsAuto);
        Assert.Equal($"{Path.GetFileName(dir)}: Moved 1 files by ByType", manual.Description);
    }

    [Fact]
    public async Task Undo_LatestRunIsAuto_UndoesAutoAndRaisesEvent()
    {
        var dir = TestData.NewTempDir("auto");
        var moved = TestData.WriteFile(dir, "moved.txt");
        var original = Path.Combine(dir, "orig.txt");
        JsonFileStore.Save("history.json", new List<OrganizeOperation>
        {
            new()
            {
                IsAuto = true, RunId = Guid.NewGuid(), SourceFolder = dir, Description = "Auto · x: Moved 1 files by ByType",
                Moves = [new FileMove { SourcePath = original, DestinationPath = moved }]
            }
        });

        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new InlineDispatcher(), dialogs, new NullThumbnails(), new NullTheme());
        dialogs.Folders.Enqueue(dir);
        vm.AddFolderCommand.Execute(null);
        await Wait.UntilAsync(() => vm.HistoryItems.Count == 1);
        var op = vm.HistoryItems[0];

        IReadOnlyList<OrganizeOperation>? undone = null;
        vm.OperationsUndone += ops => undone = ops;
        Assert.True(vm.CanUndo);

        vm.UndoCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && op.IsUndone && vm.StatusText.StartsWith("Undo"));

        Assert.True(File.Exists(original));
        Assert.Equal([op], undone);
        Assert.StartsWith("Undo complete", vm.StatusText);
    }
}
