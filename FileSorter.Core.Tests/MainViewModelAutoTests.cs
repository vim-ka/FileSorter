using FileSorter.Models;
using FileSorter.Services;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

/// <summary>Auto-organize in the main view model: settings, pauses, notifications, warnings, gate.</summary>
public sealed class MainViewModelAutoTests : TempDataDirTest
{
    private readonly VmHost _host = new();

    protected override void BeforeDispose() => _host.Dispose();

    private string NewFolder(string name)
    {
        var dir = Path.Combine(TempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A file old enough to pass the auto-organize stability window.</summary>
    private static string OldFile(string dir, string name)
    {
        var path = TestData.WriteFile(dir, name);
        File.SetLastWriteTime(path, DateTime.Now.AddHours(-1));
        return path;
    }

    private static FolderProfile AutoProfile(string dir, bool paused = false)
    {
        var p = FolderProfile.CreateDefault(dir);
        p.Auto.Enabled = true;
        p.Auto.IsPaused = paused;
        return p;
    }

    private static void SeedSettings(bool pauseAll, params FolderProfile[] profiles) =>
        SettingsService.Save(new AppSettings { Folders = [.. profiles], PauseAll = pauseAll });

    /// <summary>The folder's profile as saved on disk; an empty profile until the (background) save lands.</summary>
    private static FolderProfile SavedProfile(string dir) =>
        SettingsService.Load().Folders.SingleOrDefault(f => PathKey.Equal(f.FullPath, dir)) ?? new FolderProfile();

    [Fact]
    public async Task UndoOperationOfAutoFolder_PausesItsAuto()
    {
        var dir = NewFolder("Downloads");
        var moved = TestData.WriteFile(dir, "moved.txt");
        var original = Path.Combine(dir, "orig.txt");
        JsonFileStore.Save("history.json", new List<OrganizeOperation>
        {
            new() { RunId = Guid.NewGuid(), SourceFolder = dir, Description = "manual",
                Moves = [new FileMove { SourcePath = original, DestinationPath = moved }] }
        });
        SeedSettings(false, AutoProfile(dir));

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folder = vm.Folders.Single();
            vm.SelectedFolder = folder;
            Assert.Equal(AutoStatus.On, folder.AutoStatus);
            Assert.False(vm.AutoIsPaused);

            await Wait.UntilAsync(() => vm.HistoryItems.Count == 1
                                        && vm.UndoOperationCommand.CanExecute(vm.HistoryItems[0]));
            vm.UndoOperationCommand.Execute(vm.HistoryItems[0]);

            await Wait.UntilAsync(() => File.Exists(original) && vm.AutoIsPaused);
            Assert.True(folder.Profile.Auto.IsPaused);
            Assert.Equal(AutoStatus.Paused, folder.AutoStatus);
            await Wait.UntilAsync(() => SavedProfile(dir).Auto.IsPaused);
        });
    }

    [Fact]
    public async Task ResumeAuto_ClearsPause()
    {
        var dir = NewFolder("Downloads");
        var file = OldFile(dir, "a.txt");
        SeedSettings(false, AutoProfile(dir, paused: true));

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folder = vm.Folders.Single();
            vm.SelectedFolder = folder;
            Assert.True(vm.AutoIsPaused);
            Assert.Equal(AutoStatus.Paused, folder.AutoStatus);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ResumeAutoCommand.Execute(null);

            Assert.False(vm.AutoIsPaused);
            Assert.False(folder.Profile.Auto.IsPaused);
            Assert.Equal(AutoStatus.On, folder.AutoStatus);
            Assert.Contains(nameof(vm.AutoIsPaused), raised);
            // what accumulated during the pause is organized right away
            await Wait.UntilAsync(() => File.Exists(Path.Combine(dir, "Documents", "a.txt")));
            Assert.False(File.Exists(file));
            await Wait.UntilAsync(() => SavedProfile(dir).Auto is { Enabled: true, IsPaused: false });
        });
    }

    [Fact]
    public async Task ResumeAll_KeepsPerFolderPauses()
    {
        var a = NewFolder("A");
        var b = NewFolder("B");
        var fileA = OldFile(a, "a.txt");
        OldFile(b, "b.txt");
        SeedSettings(true, AutoProfile(a, paused: true), AutoProfile(b));

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folderA = vm.Folders.Single(f => f.FullPath == a);
            var folderB = vm.Folders.Single(f => f.FullPath == b);
            Assert.True(vm.PauseAll);
            Assert.Equal(AutoStatus.Paused, folderA.AutoStatus);
            Assert.Equal(AutoStatus.Paused, folderB.AutoStatus);

            vm.TogglePauseAllCommand.Execute(null);

            Assert.False(vm.PauseAll);
            Assert.True(folderA.Profile.Auto.IsPaused);
            Assert.Equal(AutoStatus.Paused, folderA.AutoStatus);
            Assert.Equal(AutoStatus.On, folderB.AutoStatus);
            // B is checked again now (the startup check was skipped while paused); A is left alone
            await Wait.UntilAsync(() => File.Exists(Path.Combine(b, "Documents", "b.txt")));
            Assert.True(File.Exists(fileA));
            await Wait.UntilAsync(() => !SettingsService.Load().PauseAll);
            Assert.True(SavedProfile(a).Auto.IsPaused);

            vm.TogglePauseAllCommand.Execute(null);
            Assert.True(vm.PauseAll);
            Assert.Equal(AutoStatus.Paused, folderB.AutoStatus);
        });
    }

    [Fact]
    public async Task AutoRunCompleted_NotifiesWithFolderNameAndCount()
    {
        var dir = NewFolder("Downloads");
        var files = new[] { OldFile(dir, "a.txt"), OldFile(dir, "b.txt"), OldFile(dir, "c.jpg") };

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);

            vm.AutoEnabled = true;

            await Wait.UntilAsync(() => _host.Notifications.Shown.Count == 1);
            var (title, message, onUndo) = _host.Notifications.Shown[0];
            Assert.Equal("FileSorter", title);
            Assert.Equal("Downloads: 3 files organized", message);
            Assert.All(files, f => Assert.False(File.Exists(f)));
            await Wait.UntilAsync(() => vm.HistoryItems.Count == 1);
            var op = vm.HistoryItems[0];
            Assert.True(op.IsAuto);

            onUndo();

            await Wait.UntilAsync(() => op.IsUndone && !vm.IsOrganizing);
            Assert.All(files, f => Assert.True(File.Exists(f)));
            Assert.True(vm.AutoIsPaused); // undone through the usual path: the folder is paused
        });
    }

    [Fact]
    public async Task Undo_AutoRunStartingAsSoonAsTheGateFrees_DoesNotResortRestoredFiles()
    {
        var dir = NewFolder("Downloads");
        var files = new[] { OldFile(dir, "a.txt"), OldFile(dir, "b.txt") };

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);
            vm.AutoEnabled = true;
            await Wait.UntilAsync(() => vm.HistoryItems.Count == 1 && !vm.Gate.IsBusy(dir)
                                        && vm.UndoOperationCommand.CanExecute(vm.HistoryItems[0]));
            var op = vm.HistoryItems[0];

            // Stands in for a schedule tick / debounce / watcher run firing the moment the undo
            // releases the folder (restored files keep their old mtime, so they are "stable").
            var armed = true;
            var pausedWhenFreed = false;
            Task<OrganizeOperation?>? raced = null;
            vm.Gate.Changed += _ =>
            {
                if (!armed || vm.Gate.IsBusy(dir)) return;
                armed = false;
                pausedWhenFreed = vm.SelectedFolder!.Profile.Auto.IsPaused
                                  && vm.SelectedFolder.AutoStatus == AutoStatus.Paused;
                raced = vm.Engine.RunNowAsync(dir);
            };

            vm.UndoOperationCommand.Execute(op);

            await Wait.UntilAsync(() => raced is { IsCompleted: true } && op.IsUndone && !vm.IsOrganizing);
            Assert.True(pausedWhenFreed, "the folder must be paused before the undo releases it");
            Assert.Null(await raced!);
            Assert.All(files, f => Assert.True(File.Exists(f)));
            Assert.True(vm.AutoIsPaused);
            Assert.Equal(AutoStatus.Paused, vm.SelectedFolder!.AutoStatus);
        });
    }

    [Fact]
    public async Task EnablingAuto_ClearsPauseAndRuns()
    {
        var dir = NewFolder("Downloads");
        var file = OldFile(dir, "a.txt");
        var profile = FolderProfile.CreateDefault(dir);
        profile.Auto.IsPaused = true; // paused earlier, then auto was turned off
        SeedSettings(false, profile);

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folder = vm.Folders.Single();
            vm.SelectedFolder = folder;
            Assert.Equal(AutoStatus.Off, folder.AutoStatus);

            vm.AutoEnabled = true;

            Assert.False(folder.Profile.Auto.IsPaused);
            Assert.False(vm.AutoIsPaused);
            Assert.Equal(AutoStatus.On, folder.AutoStatus);
            await Wait.UntilAsync(() => File.Exists(Path.Combine(dir, "Documents", "a.txt")));
            Assert.False(File.Exists(file));
            await Wait.UntilAsync(() => SavedProfile(dir).Auto is { Enabled: true, IsPaused: false });
        });
    }

    [Fact]
    public void AutoNotificationMessage_CountsMovedFilesNotDuplicates_SingularForOne()
    {
        static OrganizeOperation Op(params bool[] duplicates) => new()
        {
            Moves = [.. duplicates.Select(d => new FileMove { WasDuplicate = d })]
        };

        Assert.Equal("Downloads: 1 file organized",
            MainViewModel.AutoNotificationMessage("Downloads", [Op(false, true)]));
        Assert.Equal("Downloads: 3 files organized",
            MainViewModel.AutoNotificationMessage("Downloads", [Op(false, false), Op(true, false)]));
    }

    [Fact]
    public async Task AutoRunInSelectedFolder_RefreshesFiles()
    {
        var dir = NewFolder("Downloads");
        OldFile(dir, "a.txt");
        OldFile(dir, "b.txt");

        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);
            vm.IncludeSubfolders = false;
            await Wait.UntilAsync(() => !vm.IsScanning && vm.CurrentFolderFileCount == 2);

            vm.AutoEnabled = true;

            // the run moves both files into subfolders; the root-only rescan finds none
            await Wait.UntilAsync(() => !vm.IsScanning && vm.CurrentFolderFileCount == 0);
            Assert.Empty(vm.PreviewItems);
        });
    }

    [Fact]
    public Task KeepRunningInBackground_FollowsAutoEnabled() => _host.Run(() =>
    {
        var vm = _host.NewVm();
        _host.AddFolder(vm, NewFolder("A"));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.KeepRunningInBackground);

        vm.AutoEnabled = true;
        Assert.True(vm.KeepRunningInBackground);
        Assert.Contains(nameof(vm.KeepRunningInBackground), raised);
        Assert.Equal(AutoStatus.On, vm.SelectedFolder!.AutoStatus);

        raised.Clear();
        vm.AutoEnabled = false;
        Assert.False(vm.KeepRunningInBackground);
        Assert.Contains(nameof(vm.KeepRunningInBackground), raised);
        Assert.Equal(AutoStatus.Off, vm.SelectedFolder!.AutoStatus);
    });

    [Fact]
    public async Task AutoSettings_WriteIntoProfileAndPersistImmediately()
    {
        var dir = NewFolder("A");
        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folder = _host.AddFolder(vm, dir);
            vm.AutoEnabled = true;
            vm.AutoOnNewFiles = false;
            vm.AutoScheduleEnabled = true;
            vm.AutoScheduleInterval = 15;
            vm.AutoScheduleUnit = ScheduleUnit.Minutes;

            var auto = folder.Profile.Auto;
            Assert.True(auto is { Enabled: true, OnNewFiles: false, ScheduleEnabled: true, ScheduleInterval: 15, ScheduleUnit: ScheduleUnit.Minutes });
            await Wait.UntilAsync(() => SavedProfile(dir).Auto is { Enabled: true, ScheduleInterval: 15, ScheduleUnit: ScheduleUnit.Minutes });

            vm.AutoScheduleInterval = 0; // at least one unit
            Assert.Equal(1, vm.AutoScheduleInterval);
        });
    }

    [Fact]
    public async Task AddAndRemoveFolder_PersistImmediately()
    {
        var dir = NewFolder("A");
        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            var folder = _host.AddFolder(vm, dir);
            await Wait.UntilAsync(() => SettingsService.Load().Folders.Count == 1);

            vm.RemoveFolderCommand.Execute(folder);
            await Wait.UntilAsync(() => SettingsService.Load().Folders.Count == 0);
        });
    }

    [Fact]
    public Task StartWithWindows_CallsAutostartService() => _host.Run(() =>
    {
        var vm = _host.NewVm();
        Assert.False(vm.StartWithWindows);

        vm.StartWithWindows = true;

        Assert.Equal([true], _host.Autostart.Calls);
        Assert.True(vm.StartWithWindows);
        vm.Shutdown();
        Assert.True(SettingsService.Load().StartWithWindows);

        vm.StartWithWindows = false;
        Assert.Equal([true, false], _host.Autostart.Calls);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task StartWithWindows_AtStartup_FollowsAutostartServiceNotSettings(bool saved, bool actual) => _host.Run(() =>
    {
        SettingsService.Save(new AppSettings { StartWithWindows = saved });
        _host.Autostart.IsEnabled = actual;

        var vm = _host.NewVm();

        Assert.Equal(actual, vm.StartWithWindows);
        Assert.Empty(_host.Autostart.Calls);
    });

    [Fact]
    public Task StartWithWindows_FailedChange_KeepsActualState() => _host.Run(() =>
    {
        var vm = _host.NewVm();
        _host.Autostart.FailWrites = true;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.StartWithWindows = true;

        Assert.Equal([true], _host.Autostart.Calls);
        Assert.False(vm.StartWithWindows);
        Assert.Contains(nameof(MainViewModel.StartWithWindows), raised); // the checkbox snaps back
    });

    [Fact]
    public Task AutoWarnings_DestinationInsideOtherAutoFolder() => _host.Run(() =>
    {
        var downloads = NewFolder("Downloads");
        var vm = _host.NewVm();
        _host.AddFolder(vm, downloads);
        vm.AutoEnabled = true;
        _host.AddFolder(vm, NewFolder("Desktop"));
        Assert.Empty(vm.AutoWarnings);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.DestinationFolder = Path.Combine(downloads, "Sorted");

        var warning = Assert.Single(vm.AutoWarnings);
        Assert.Contains("Downloads", warning);
        Assert.Contains(nameof(vm.AutoWarnings), raised);
        Assert.True(vm.HasAutoWarnings);
    });

    [Fact]
    public Task AutoWarnings_OtherAutoFolderSendsIntoSelected() => _host.Run(() =>
    {
        var downloads = NewFolder("Downloads");
        var vm = _host.NewVm();
        var dl = _host.AddFolder(vm, downloads);
        vm.AutoEnabled = true;
        _host.AddFolder(vm, NewFolder("Desktop"));
        vm.AutoEnabled = true;
        vm.DestinationFolder = Path.Combine(downloads, "Sorted");

        vm.SelectedFolder = dl;

        var warning = Assert.Single(vm.AutoWarnings);
        Assert.Contains("Desktop", warning);
    });

    [Fact]
    public async Task OrganizeAndUndo_DisabledWhileFolderIsBusy()
    {
        var dir = NewFolder("A");
        TestData.WriteFile(dir, "a.txt");
        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);
            await Wait.UntilAsync(() => vm.OrganizeCommand.CanExecute(null));
            var changed = 0;
            vm.OrganizeCommand.CanExecuteChanged += (_, _) => changed++;

            Assert.True(vm.Gate.TryEnter(dir)); // e.g. an auto run in progress
            await Wait.UntilAsync(() => changed > 0);
            Assert.False(vm.OrganizeCommand.CanExecute(null));

            vm.Gate.Exit(dir);
            await Wait.UntilAsync(() => vm.OrganizeCommand.CanExecute(null));

            vm.OrganizeCommand.Execute(null);
            await Wait.UntilAsync(() => vm.CanUndo && !vm.IsOrganizing && vm.UndoCommand.CanExecute(null));
            Assert.True(vm.Gate.TryEnter(dir));
            await Wait.UntilAsync(() => !vm.UndoCommand.CanExecute(null));
            Assert.False(vm.UndoOperationCommand.CanExecute(vm.HistoryItems[0]));
            vm.Gate.Exit(dir);
            await Wait.UntilAsync(() => vm.UndoCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task ManualOrganize_HoldsTheGateOfItsFolder()
    {
        var dir = NewFolder("A");
        TestData.WriteFile(dir, "a.txt");
        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);
            await Wait.UntilAsync(() => vm.OrganizeCommand.CanExecute(null));
            var busySeen = false;
            vm.Gate.Changed += f => busySeen |= PathKey.Equal(f, dir) && vm.Gate.IsBusy(dir);

            vm.OrganizeCommand.Execute(null);
            await Wait.UntilAsync(() => !vm.IsOrganizing && vm.CanUndo);

            Assert.True(busySeen);
            Assert.False(vm.Gate.IsBusy(dir));
        });
    }

    [Fact]
    public Task Shutdown_SavesSettings() => _host.Run(() =>
    {
        var vm = _host.NewVm();
        _host.AddFolder(vm, NewFolder("A"));
        vm.CopyMode = true;

        vm.Shutdown();
        vm.Shutdown(); // idempotent

        Assert.True(SettingsService.Load().Folders.Single().CopyMode);
    });

    [Fact]
    public async Task Shutdown_DuringManualOrganize_RecordsWhatWasMovedWithoutDeadlock()
    {
        var dir = NewFolder("A");
        for (int i = 0; i < 300; i++)
            TestData.WriteFile(dir, $"f{i:D3}.txt");
        await _host.Run(async () =>
        {
            var vm = _host.NewVm();
            _host.AddFolder(vm, dir);
            await Wait.UntilAsync(() => vm.OrganizeCommand.CanExecute(null) && vm.CurrentFolderFileCount == 300);

            vm.OrganizeCommand.Execute(null);
            await Wait.UntilAsync(() => Directory.Exists(Path.Combine(dir, "Documents")));
            vm.Shutdown(); // on the UI thread, while the run's continuations can't run here
        });

        // Every file that left the folder is in the saved history, so it can be undone
        var left = 300 - Directory.GetFiles(dir).Length;
        var saved = await HistoryService.LoadAsync();
        Assert.Equal(left, saved.Sum(op => op.Moves.Count));
    }
}
