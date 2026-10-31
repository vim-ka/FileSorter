using FileSorter.Models;
using FileSorter.Services;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class MultiFolderTests : TempDataDirTest
{
    private readonly VmHost _host = new();

    protected override void BeforeDispose() => _host.Dispose();

    private FakeDialogs _dialogs => _host.Dialogs;
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    private static readonly RgbColor[] Colors = [new(1, 1, 1), new(2, 2, 2), new(3, 3, 3)];

    private MainViewModel NewVm() =>
        _host.NewVm();

    private FolderViewModel AddFolder(MainViewModel vm, string path)
    {
        _dialogs.Folders.Enqueue(path);
        vm.AddFolderCommand.Execute(null);
        return vm.SelectedFolder!;
    }

    private OrganizeRequest Request(string source, OrganizeMethod method) =>
        new(source, null, method, null, "yyyy-MM", _categories, null, [],
            UnmatchedFilesMode.OrganizeNormally, false, false);

    /// <summary>Two folders with real files: A organized by type, B by extension; both checked.</summary>
    private async Task<(MainViewModel Vm, FolderViewModel A, FolderViewModel B)> TwoCheckedFoldersAsync()
    {
        var dirA = TestData.NewTempDir("A");
        TestData.WriteFile(dirA, "a.txt", "aaa");
        TestData.WriteFile(dirA, "b.jpg", "bbbb");
        var dirB = TestData.NewTempDir("B");
        TestData.WriteFile(dirB, "c.txt", "ccccc");
        TestData.WriteFile(dirB, "d.jpg", "dddddd");

        var vm = NewVm();
        var a = AddFolder(vm, dirA);
        vm.SelectedMethod = OrganizeMethod.ByType;
        var b = AddFolder(vm, dirB);
        vm.SelectedMethod = OrganizeMethod.ByExtension;

        a.IsChecked = true;
        b.IsChecked = true;
        await Wait.UntilAsync(() => !vm.IsScanning && vm.PreviewItems.Count == 2
                                    && vm.StatusText.Contains("4 of 4 files will be organized"));
        return (vm, a, b);
    }

    [Fact]
    public void Build_TwoSources_OneRootPerFolder_StatsSummed()
    {
        var sources = new List<PreviewSource>
        {
            new("Downloads", Request(@"C:\dl", OrganizeMethod.ByExtension),
                [TestData.Item(@"C:\dl\a.txt"), TestData.Item(@"C:\dl\b.txt"), TestData.Item(@"C:\dl\c.jpg")]),
            new("Desktop", Request(@"C:\desk", OrganizeMethod.ByExtension),
                [TestData.Item(@"C:\desk\d.txt")])
        };

        var result = PreviewBuilder.Build(sources, Colors, _ => null);

        Assert.Equal(["Downloads \u2192 Folder", "Desktop \u2192 Folder"], result.Roots.Select(r => r.Name));
        Assert.Equal(["JPG", "TXT"], result.Roots[0].Children.Select(c => c.Name).OrderBy(n => n));
        Assert.Equal(["TXT"], result.Roots[1].Children.Select(c => c.Name));

        Assert.Equal(4, result.FileCount);
        var txt = result.Stats.Single(s => s.Name == "TXT");
        var jpg = result.Stats.Single(s => s.Name == "JPG");
        Assert.Equal(2, result.Stats.Count);
        Assert.Equal(3, txt.Count);
        Assert.Equal(1, jpg.Count);
        Assert.Equal(75, txt.Percentage, 3);
        Assert.Equal(360, result.Stats.Sum(s => s.SweepAngle), 3);

        Assert.Equal(["Downloads/JPG", "Downloads/TXT", "Desktop/TXT"],
            result.ViewGroups.Select(g => g.Key).OrderBy(k => k.StartsWith("Desktop")).ThenBy(k => k));
        Assert.Equal(2, result.ViewGroups.Single(g => g.Key == "Downloads/TXT").FileCount);
    }

    [Fact]
    public void Build_ManyGroups_StatsKeepLargestAndSumTheRest()
    {
        // 41 groups: BIG with 10 files, then 40 single-file extensions
        var files = Enumerable.Range(0, 10).Select(i => TestData.Item($@"C:\dl\big{i}.big"))
            .Concat(Enumerable.Range(0, 40).Select(i => TestData.Item($@"C:\dl\f{i}.e{i:D2}")))
            .ToList();
        var sources = new List<PreviewSource> { new("Downloads", Request(@"C:\dl", OrganizeMethod.ByExtension), files) };

        var result = PreviewBuilder.Build(sources, Colors, _ => null);

        Assert.Equal(41, result.GroupCount);
        Assert.Equal(PreviewBuilder.MaxStatsGroups, result.Stats.Count);
        Assert.Contains(result.Stats, s => s.Name == "BIG" && s.Count == 10);
        var rest = result.Stats[^1];
        int hidden = 41 - (PreviewBuilder.MaxStatsGroups - 1);
        Assert.Equal($"+{hidden} more", rest.Name);
        Assert.Equal(hidden, rest.Count);
        Assert.Equal(50, result.Stats.Sum(s => s.Count));
        Assert.Equal(360, result.Stats.Sum(s => s.SweepAngle), 3);
        Assert.Equal(41, result.ViewGroups.Count);
    }

    [Fact]
    public void Build_ByType_StatsCountOnlyFilesThatWillBeOrganized_AndHideEmptyOnes()
    {
        // rule: jpg → destination root; other files stay in place
        var jpgRule = new RoutingRule
        {
            Conditions = [new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "jpg" }]
        }.Snapshot()!;
        var request = Request(@"C:\dl", OrganizeMethod.ByType) with
        {
            Rules = [jpgRule], Unmatched = UnmatchedFilesMode.LeaveInPlace,
            AllowedCategories = new HashSet<string> { "Images", "Documents" }
        };
        var files = new List<FileItem>
        {
            TestData.Item(@"C:\dl\a.jpg"), TestData.Item(@"C:\dl\b.jpg"),
            TestData.Item(@"C:\dl\c.txt"), TestData.Item(@"C:\dl\d.zip")
        };

        var result = PreviewBuilder.Build([new("Downloads", request, files)], Colors, _ => null);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(2, result.Stats.Single(s => s.Name == "Images").Count);
        Assert.DoesNotContain(result.Stats, s => s.Name == "Documents"); // all left in place: nothing to show
        var archives = result.Stats.Single(s => s.Name == "Archives"); // filtered out: shows what it would add
        Assert.False(archives.IsSelected);
        Assert.Equal(1, archives.Count);
    }

    [Fact]
    public void Build_OneSource_KeepsDestinationRootAndByTypeStats()
    {
        var sources = new List<PreviewSource>
        {
            new("Downloads", Request(@"C:\dl", OrganizeMethod.ByType),
                [TestData.Item(@"C:\dl\a.txt"), TestData.Item(@"C:\dl\b.jpg")])
        };

        var result = PreviewBuilder.Build(sources, Colors, _ => null);

        Assert.Equal("Destination \u2192 Folder", Assert.Single(result.Roots).Name);
        Assert.Equal(["Documents", "Images"], result.Stats.Select(s => s.Name));
        Assert.Equal(["Documents", "Images"], result.ViewGroups.Select(g => g.Key));
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public Task OrganizeTargets_OnlyCheckedFolders_AddedFoldersAreChecked() => _host.Run(() =>
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var b = AddFolder(vm, TestData.NewTempDir("B"));
        var c = AddFolder(vm, TestData.NewTempDir("C"));
        Assert.Equal([c, b, a], vm.OrganizeTargets);

        a.IsChecked = b.IsChecked = c.IsChecked = false;
        Assert.Same(c, vm.SelectedFolder);
        Assert.Empty(vm.OrganizeTargets); // selected but unchecked: not processed
        Assert.Equal("ORGANIZE", vm.OrganizeButtonText);

        a.IsChecked = true;
        b.IsChecked = true;
        // list order (newest on top), and the selected but unchecked folder is not included
        Assert.Equal([b, a], vm.OrganizeTargets);
        Assert.Equal("ORGANIZE 2 FOLDERS", vm.OrganizeButtonText);

        b.IsChecked = false;
        Assert.Equal([a], vm.OrganizeTargets);
        Assert.Equal("ORGANIZE", vm.OrganizeButtonText);
    });

    [Fact]
    public Task Organize_TwoCheckedFolders_EachWithOwnProfile_OneRunTwoOperations() => _host.Run(async () =>
    {
        var (vm, a, b) = await TwoCheckedFoldersAsync();
        Assert.Equal(["B", "A"], vm.PreviewItems.Select(p => p.Name.Split('_')[1]));

        vm.OrganizeCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && !vm.IsScanning && vm.HistoryItems.Count == 2);

        Assert.True(File.Exists(Path.Combine(a.FullPath, "Documents", "a.txt")));
        Assert.True(File.Exists(Path.Combine(a.FullPath, "Images", "b.jpg")));
        Assert.True(File.Exists(Path.Combine(b.FullPath, "TXT", "c.txt")));
        Assert.True(File.Exists(Path.Combine(b.FullPath, "JPG", "d.jpg")));

        var ops = vm.HistoryItems.ToList();
        Assert.Equal(ops[0].RunId, ops[1].RunId);
        Assert.NotEqual(Guid.Empty, ops[0].RunId);
        Assert.Equal(new[] { a.FullPath, b.FullPath }.OrderBy(p => p), ops.Select(o => o.SourceFolder).OrderBy(p => p));
        Assert.Equal($"{b.Name}: 2 moved; {a.Name}: 2 moved", vm.StatusText);
    });

    [Fact]
    public Task Organize_OneFolderFails_OthersStillProcessed() => _host.Run(async () =>
    {
        var (vm, a, b) = await TwoCheckedFoldersAsync();
        Directory.Delete(a.FullPath, recursive: true);

        vm.OrganizeCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && !vm.IsScanning && vm.StatusText.Contains("error"));

        Assert.True(File.Exists(Path.Combine(b.FullPath, "TXT", "c.txt")));
        Assert.True(File.Exists(Path.Combine(b.FullPath, "JPG", "d.jpg")));
        Assert.StartsWith($"{b.Name}: 2 moved; {a.Name}: error", vm.StatusText);
        Assert.Empty(_dialogs.Errors);
    });

    [Fact]
    public Task Undo_AfterMultiFolderRun_RestoresBoth() => _host.Run(async () =>
    {
        var (vm, a, b) = await TwoCheckedFoldersAsync();
        vm.OrganizeCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && !vm.IsScanning && vm.HistoryItems.Count == 2);

        vm.UndoCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && vm.HistoryItems.All(o => o.IsUndone));

        Assert.True(File.Exists(Path.Combine(a.FullPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(a.FullPath, "b.jpg")));
        Assert.True(File.Exists(Path.Combine(b.FullPath, "c.txt")));
        Assert.True(File.Exists(Path.Combine(b.FullPath, "d.jpg")));
        Assert.False(vm.CanUndo);
    });

    [Fact]
    public Task Undo_RunWhoseLaterOperationMovedEarlierOutputs_UndoneInOneClick() => _host.Run(async () =>
    {
        var dir = TestData.NewTempDir("chain");
        var original = Path.Combine(dir, "orig.txt");
        var middle = Path.Combine(dir, "mid", "orig.txt");
        var final = TestData.WriteFile(dir, "final.txt");
        var run = Guid.NewGuid();
        JsonFileStore.Save("history.json", new List<OrganizeOperation>
        {
            new() { RunId = run, SourceFolder = dir, Description = "first",
                Moves = [new FileMove { SourcePath = original, DestinationPath = middle }] },
            new() { RunId = run, SourceFolder = dir, Description = "second",
                Moves = [new FileMove { SourcePath = middle, DestinationPath = final }] }
        });

        var vm = NewVm();
        AddFolder(vm, dir);
        await Wait.UntilAsync(() => vm.HistoryItems.Count == 2);

        vm.UndoCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && vm.StatusText.StartsWith("Undo"));

        Assert.True(File.Exists(original));
        Assert.False(File.Exists(final));
        Assert.All(vm.HistoryItems, o => Assert.True(o.IsUndone));
    });

    [Fact]
    public Task UncheckingFolder_RemovesItFromPreview() => _host.Run(async () =>
    {
        var (vm, a, b) = await TwoCheckedFoldersAsync();

        a.IsChecked = false;
        await Wait.UntilAsync(() => !vm.IsScanning && vm.PreviewItems.Count == 1
                                    && vm.StatusText.Contains("2 of 2 files will be organized"));

        // one target left: back to the single-folder layout, B's own profile (by extension)
        Assert.Equal("Destination \u2192 Folder", vm.PreviewItems[0].Name);
        Assert.Equal(["JPG", "TXT"], vm.PreviewItems[0].Children.Select(c => c.Name).OrderBy(n => n));
    });

    [Fact]
    public Task CheckedFolder_IncludeSubfoldersComesFromItsOwnProfile() => _host.Run(async () =>
    {
        var dirA = TestData.NewTempDir("A");
        TestData.WriteFile(dirA, "top.txt");
        Directory.CreateDirectory(Path.Combine(dirA, "sub"));
        TestData.WriteFile(Path.Combine(dirA, "sub"), "deep.txt");
        var dirB = TestData.NewTempDir("B");
        TestData.WriteFile(dirB, "b.txt");

        var vm = NewVm();
        var a = AddFolder(vm, dirA);
        vm.IncludeSubfolders = false;
        Directory.CreateDirectory(Path.Combine(dirB, "sub"));
        TestData.WriteFile(Path.Combine(dirB, "sub"), "deep.txt");
        var b = AddFolder(vm, dirB);
        Assert.True(vm.IncludeSubfolders); // B keeps the default (recursive)

        a.IsChecked = true;
        b.IsChecked = true;
        await Wait.UntilAsync(() => !vm.IsScanning && vm.StatusText.Contains("3 of 3 files will be organized"));
        Assert.Equal(1, a.FileCount);
        Assert.Equal(2, b.FileCount);
    });

    [Fact]
    public Task WhileOrganizing_FolderSelectionAndRemovalAreIgnored() => _host.Run(() =>
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        vm.IsOrganizing = true;
        vm.SelectFolderCommand.Execute(a);
        vm.RemoveFolderCommand.Execute(b);

        Assert.Same(b, vm.SelectedFolder);
        Assert.Equal(2, vm.Folders.Count);
    });
}
