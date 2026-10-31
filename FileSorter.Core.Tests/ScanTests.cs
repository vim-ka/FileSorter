using System.Collections.Concurrent;
using FileSorter.Abstractions;
using FileSorter.Services;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class ScanTests : TempDataDirTest
{
    /// <summary>Queues UI actions instead of running them, so a test controls when a batch lands.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        public ConcurrentQueue<(Action Action, TaskCompletionSource Done)> Queue { get; } = new();

        public Task InvokeAsync(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Enqueue((action, tcs));
            return tcs.Task;
        }

        public async Task RunOneAsync()
        {
            await WaitUntilAsync(() => !Queue.IsEmpty);
            Assert.True(Queue.TryDequeue(out var item));
            item.Action();
            item.Done.SetResult();
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000) =>
        Wait.UntilAsync(condition, timeoutMs);

    private static MainViewModel NewVm(IUiDispatcher dispatcher, FakeDialogs dialogs) =>
        new(dispatcher, dialogs, new NullThumbnails(), new NullTheme(), new FakeNotifications(), new FakeAutostart());

    private static void AddFolder(MainViewModel vm, FakeDialogs dialogs, string path)
    {
        dialogs.Folders.Enqueue(path);
        vm.AddFolderCommand.Execute(null);
    }

    [Fact]
    public async Task SwitchingFolderMidScan_DoesNotLeakOldFolderBatchIntoNewList()
    {
        var dirA = TestData.NewTempDir("A");
        for (int i = 0; i < 60; i++) TestData.WriteFile(dirA, $"a{i}.txt");
        var dirB = TestData.NewTempDir("B");
        for (int i = 0; i < 3; i++) TestData.WriteFile(dirB, $"b{i}.txt");

        var dispatcher = new QueuedDispatcher();
        var dialogs = new FakeDialogs();
        var vm = NewVm(dispatcher, dialogs);

        AddFolder(vm, dialogs, dirA);
        await WaitUntilAsync(() => !dispatcher.Queue.IsEmpty); // A's first batch is posted, not yet applied

        vm.Folders.Single(f => f.FullPath == dirA).IsChecked = false; // cancels A's scan
        AddFolder(vm, dialogs, dirB); // starts B

        await dispatcher.RunOneAsync(); // the stale batch from A lands now
        await dispatcher.RunOneAsync(); // B's batch
        await WaitUntilAsync(() => !vm.IsScanning);

        Assert.Equal(3, vm.CurrentFolderFileCount);
    }

    [Fact]
    public async Task FileList_DuringScan_IsNotRebuiltWithinTheInterval_AndIsCompleteAfterIt()
    {
        var dir = TestData.NewTempDir("big");
        for (int i = 0; i < 120; i++) TestData.WriteFile(dir, $"f{i}.txt"); // 3 batches of 50
        var saved = MainViewModel.FlatListScanInterval;
        MainViewModel.FlatListScanInterval = TimeSpan.FromHours(1);
        try
        {
            var dispatcher = new QueuedDispatcher();
            var dialogs = new FakeDialogs();
            var vm = NewVm(dispatcher, dialogs);
            int Files() => vm.FlatViewItems.Count(f => !f.IsHeader);

            AddFolder(vm, dialogs, dir);
            await dispatcher.RunOneAsync();
            await WaitUntilAsync(() => Files() == 50); // the first refresh of a scan shows the list

            await dispatcher.RunOneAsync();
            await WaitUntilAsync(() => vm.StatusText.StartsWith("100 of 100"));
            Assert.Equal(50, Files()); // the preview refreshed, the file list didn't

            await dispatcher.RunOneAsync();
            await WaitUntilAsync(() => !vm.IsScanning && Files() == 120);
        }
        finally
        {
            MainViewModel.FlatListScanInterval = saved;
        }
    }

    [Fact]
    public async Task OrganizeResultMessage_SurvivesTheRescanThatFollows()
    {
        var dir = TestData.NewTempDir("status");
        TestData.WriteFile(dir, "a.txt");
        TestData.WriteFile(dir, "b.txt");

        using var host = new VmHost();
        await host.Run(async () =>
        {
            var vm = host.NewVm();
            host.AddFolder(vm, dir);
            await WaitUntilAsync(() => vm.StatusText.Contains("will be organized"));

            vm.OrganizeCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsOrganizing && !vm.IsScanning);
            await Task.Delay(1000); // let the post-organize rescan and preview refresh settle

            Assert.StartsWith("Done! 2 files moved", vm.StatusText);
        });
    }

    [Fact]
    public async Task Scan_SkipsFilesInsideDuplicatesFolder()
    {
        var dir = TestData.NewTempDir("dup");
        TestData.WriteFile(dir, "keep.txt");
        var dupDir = Path.Combine(dir, FileOrganizerService.DuplicatesFolderName);
        Directory.CreateDirectory(dupDir);
        TestData.WriteFile(dupDir, "skip.txt");

        var found = new List<string>();
        await new FileOrganizerService().ScanFolderAsync(dir, true,
            batch => { found.AddRange(batch.Select(f => f.FileName)); return Task.CompletedTask; },
            50, CancellationToken.None);

        Assert.Equal(["keep.txt"], found);
    }
}
