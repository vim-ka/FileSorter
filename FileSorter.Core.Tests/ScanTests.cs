using System.Collections.Concurrent;
using FileSorter.Abstractions;
using FileSorter.Services;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class ScanTests
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

    private sealed class NoDialogs : IDialogService
    {
        public string? PickFolder(string title) => null;
        public void ShowError(string title, string message) { }
    }

    private sealed class NoThumbnails : IThumbnailProvider
    {
        public object? GetOrRequest(string path, DateTime dateModified, int size, Action<object> onLoaded) => null;
        public void CancelPending() { }
    }

    private sealed class NoTheme : IThemeService
    {
        public void ApplyTheme(bool isDark) { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for condition");
            await Task.Delay(10);
        }
    }

    public ScanTests()
    {
        TestData.RedirectAppData();
        JsonFileStore.DataDir = TestData.NewTempDir("scan");
    }

    [Fact]
    public async Task SwitchingFolderMidScan_DoesNotLeakOldFolderBatchIntoNewList()
    {
        var dirA = TestData.NewTempDir("A");
        for (int i = 0; i < 60; i++) TestData.WriteFile(dirA, $"a{i}.txt");
        var dirB = TestData.NewTempDir("B");
        for (int i = 0; i < 3; i++) TestData.WriteFile(dirB, $"b{i}.txt");

        var dispatcher = new QueuedDispatcher();
        var vm = new MainViewModel(dispatcher, new NoDialogs(), new NoThumbnails(), new NoTheme());

        vm.SourceFolder = dirA;
        await WaitUntilAsync(() => !dispatcher.Queue.IsEmpty); // A's first batch is posted, not yet applied

        vm.SourceFolder = dirB; // cancels A, resets the list, starts B

        await dispatcher.RunOneAsync(); // the stale batch from A lands now
        await dispatcher.RunOneAsync(); // B's batch
        await WaitUntilAsync(() => !vm.IsScanning);

        Assert.Equal(3, vm.CurrentFolderFileCount);
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
