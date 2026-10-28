using FileSorter.Models;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class MainViewModelProfileTests : TempDataDirTest
{
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel NewVm() =>
        new(new InlineDispatcher(), _dialogs, new NullThumbnails(), new NullTheme());

    private FolderViewModel AddFolder(MainViewModel vm, string path)
    {
        _dialogs.Folders.Enqueue(path);
        vm.AddFolderCommand.Execute(null);
        return vm.SelectedFolder!;
    }

    private static FolderViewModel Find(MainViewModel vm, string path) =>
        vm.Folders.Single(f => f.FullPath == path);

    [Fact]
    public void SelectFolder_LoadsItsProfileIntoProperties()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        vm.CopyMode = true;
        vm.SelectedMethod = OrganizeMethod.ByDate;
        var b = AddFolder(vm, TestData.NewTempDir("B"));
        vm.CopyMode = false;
        vm.SelectedMethod = OrganizeMethod.ByExtension;

        vm.SelectFolderCommand.Execute(a);
        Assert.True(vm.CopyMode);
        Assert.False(vm.MoveMode);
        Assert.Equal(OrganizeMethod.ByDate, vm.SelectedMethod);

        vm.SelectFolderCommand.Execute(b);
        Assert.False(vm.CopyMode);
        Assert.True(vm.MoveMode);
        Assert.Equal(OrganizeMethod.ByExtension, vm.SelectedMethod);
        Assert.Same(b, vm.SelectedFolder);
        Assert.True(b.IsSelected);
        Assert.False(a.IsSelected);
        Assert.Equal(b.FullPath, vm.SourceFolder);
    }

    [Fact]
    public void ChangingProperty_WritesToSelectedProfileOnly()
    {
        var vm = NewVm();
        var b = AddFolder(vm, TestData.NewTempDir("B"));
        var a = AddFolder(vm, TestData.NewTempDir("A"));

        vm.SelectFolderCommand.Execute(a);
        vm.CopyMode = true;
        vm.SelectFolderCommand.Execute(b);

        Assert.False(vm.CopyMode);
        Assert.False(b.Profile.CopyMode);
        Assert.True(a.Profile.CopyMode);
    }

    [Fact]
    public void CategoryToggle_PersistsInProfile()
    {
        var path = TestData.NewTempDir("A");
        var vm = NewVm();
        var a = AddFolder(vm, path);

        vm.ToggleCategoryCommand.Execute("Images");
        Assert.Contains("Images", a.Profile.DisabledCategories);

        vm.SaveSettings();
        var vm2 = NewVm();
        vm2.SelectFolderCommand.Execute(Find(vm2, path));

        Assert.False(vm2.Categories.Single(c => c.Name == "Images").IsSelected);
        Assert.All(vm2.Categories.Where(c => c.Name != "Images"), c => Assert.True(c.IsSelected));
    }

    [Fact]
    public void CategoryToggle_DoesNotLeakIntoOtherFolder()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        vm.ToggleCategoryCommand.Execute("Images");
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        Assert.True(vm.Categories.Single(c => c.Name == "Images").IsSelected);
        Assert.Empty(b.Profile.DisabledCategories);

        vm.ToggleCategoryCommand.Execute("Images"); // disables Images on B (now selected)
        vm.SelectFolderCommand.Execute(a);
        Assert.False(vm.Categories.Single(c => c.Name == "Images").IsSelected);
        Assert.Equal(["Images"], b.Profile.DisabledCategories);
    }

    [Fact]
    public void AddFolder_ManyFolders_NoneEvicted()
    {
        var vm = NewVm();
        for (int i = 0; i < 15; i++)
            AddFolder(vm, TestData.NewTempDir($"F{i}"));

        Assert.Equal(15, vm.Folders.Count);
    }

    [Fact]
    public void AddFolder_NewFolderGoesToTop()
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        Assert.Same(b, vm.Folders[0]);
    }

    [Fact]
    public void AddFolder_ExistingPathDifferentCase_SelectsExisting()
    {
        var path = TestData.NewTempDir("Case");
        var vm = NewVm();
        var a = AddFolder(vm, path);
        AddFolder(vm, TestData.NewTempDir("Other"));

        var again = AddFolder(vm, path.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Same(a, again);
        Assert.Equal(2, vm.Folders.Count);
    }

    [Fact]
    public void NewFolder_GetsBuiltInDefaults()
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        vm.CopyMode = true;
        vm.SelectedMethod = OrganizeMethod.BySize;

        var b = AddFolder(vm, TestData.NewTempDir("B"));

        Assert.False(b.Profile.CopyMode);
        Assert.False(vm.CopyMode);
        Assert.Equal(OrganizeMethod.ByType, vm.SelectedMethod);
    }

    [Fact]
    public void SaveAndReload_RestoresEveryFolderAndItsValues()
    {
        var pathA = TestData.NewTempDir("A");
        var pathB = TestData.NewTempDir("B");
        var dest = TestData.NewTempDir("Dest");
        var vm = NewVm();
        AddFolder(vm, pathA);
        vm.CopyMode = true;
        vm.SelectedMethod = OrganizeMethod.ByDate;
        vm.SelectedDateGranularity = DateGranularity.Day;
        vm.DestinationSubfolderPattern = "#year/#ext";
        vm.DestinationFolder = dest;
        vm.IncludeSubfolders = false;
        vm.SkipDuplicates = true;
        vm.ApplyFilters = false;
        AddFolder(vm, pathB);
        vm.SelectedMethod = OrganizeMethod.ByAlphabet;

        vm.SaveSettings();
        var vm2 = NewVm();

        Assert.Equal([pathB, pathA], vm2.Folders.Select(f => f.FullPath));
        Assert.Null(vm2.SelectedFolder);

        vm2.SelectFolderCommand.Execute(Find(vm2, pathA));
        Assert.True(vm2.CopyMode);
        Assert.Equal(OrganizeMethod.ByDate, vm2.SelectedMethod);
        Assert.Equal(DateGranularity.Day, vm2.SelectedDateGranularity);
        Assert.Equal("#year/#ext", vm2.DestinationSubfolderPattern);
        Assert.Equal(dest, vm2.DestinationFolder);
        Assert.False(vm2.IncludeSubfolders);
        Assert.True(vm2.SkipDuplicates);
        Assert.False(vm2.ApplyFilters);

        vm2.SelectFolderCommand.Execute(Find(vm2, pathB));
        Assert.Equal(OrganizeMethod.ByAlphabet, vm2.SelectedMethod);
        Assert.False(vm2.CopyMode);
    }

    [Fact]
    public void RemoveFolder_RemovesItAndClearsSelectionWhenSelected()
    {
        var pathA = TestData.NewTempDir("A");
        var vm = NewVm();
        var a = AddFolder(vm, pathA);
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        vm.RemoveFolderCommand.Execute(a);
        Assert.Equal([b], vm.Folders);
        Assert.Same(b, vm.SelectedFolder);

        vm.RemoveFolderCommand.Execute(b);
        Assert.Empty(vm.Folders);
        Assert.Null(vm.SelectedFolder);
        Assert.Equal("", vm.SourceFolder);

        vm.SaveSettings();
        Assert.Empty(NewVm().Folders);
    }

    [Fact]
    public void NoSelectedFolder_ChangesAreNotSaved()
    {
        var vm = NewVm();
        vm.CopyMode = true;
        vm.SaveSettings();

        Assert.Empty(NewVm().Folders);
    }

    [Theory]
    [InlineData(false, false, AutoStatus.Off)]
    [InlineData(true, false, AutoStatus.On)]
    [InlineData(true, true, AutoStatus.Paused)]
    public void FolderViewModel_RefreshStatus(bool enabled, bool paused, AutoStatus expected)
    {
        var profile = FolderProfile.CreateDefault(TestData.NewTempDir("S"));
        profile.Auto.Enabled = enabled;
        profile.Auto.IsPaused = paused;
        var f = new FolderViewModel(profile);

        f.RefreshStatus(pauseAll: false);
        Assert.Equal(expected, f.AutoStatus);

        f.RefreshStatus(pauseAll: true);
        Assert.Equal(enabled ? AutoStatus.Paused : AutoStatus.Off, f.AutoStatus);
    }
}
