using System.Collections.Concurrent;
using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

/// <summary>Exit/logoff during organize: runs stop between files and what they did stays in history.</summary>
public class OrganizeCancellationTests : TempDataDirTest
{
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    /// <summary>Reports synchronously on the organizing thread (unlike <see cref="Progress{T}"/>).</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>A UI thread that is blocked: posted continuations are queued and only run on demand.</summary>
    private sealed class BlockedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunQueued()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out var item)) item.Item1(item.Item2);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private (string Dir, List<FileItem> Files, OrganizeRequest Request) Folder(int count)
    {
        var dir = TestData.NewTempDir("src");
        var files = Enumerable.Range(0, count)
            .Select(i => TestData.ItemFromDisk(TestData.WriteFile(dir, $"f{i:D2}.txt")))
            .ToList();
        return (dir, files, OrganizeRequest.FromProfile(FolderProfile.CreateDefault(dir), _categories));
    }

    [Fact]
    public async Task CancelledMidRun_MovedFilesInHistory_UndoRestoresThem()
    {
        var (dir, files, request) = Folder(5);
        var svc = new FileOrganizerService();
        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new InlineProgress(_ => { if (++reports == 2) cts.Cancel(); });

        var op = await svc.OrganizeAsync(files, request, RunContext.NewAuto(), progress, cts.Token);

        Assert.Equal(2, op.Moves.Count);
        Assert.Equal(3, Directory.GetFiles(dir).Length);
        Assert.Same(op, Assert.Single(svc.History));
        Assert.Equal(2, Assert.Single(await HistoryService.LoadAsync()).Moves.Count);

        var undo = await svc.UndoOperationAsync(op);

        Assert.Equal(2, undo.Restored);
        Assert.Equal(5, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public async Task CancelledBeforeStart_NothingMovedNothingRecorded()
    {
        var (dir, files, request) = Folder(2);
        var svc = new FileOrganizerService();

        var op = await svc.OrganizeAsync(files, request, RunContext.NewManual(), ct: new CancellationToken(true));

        Assert.Empty(op.Moves);
        Assert.Empty(svc.History);
        Assert.Equal(2, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public async Task CompleteInFlightRuns_UiThreadBlocked_RecordsAndSavesWithoutContinuations()
    {
        var (dir, files, request) = Folder(4);
        var journal = new CopyJournal();
        var svc = new FileOrganizerService(journal);
        var copyRequest = request with { CopyMode = true };
        var ui = new BlockedContext();

        // Started "on the UI thread": the continuation that records the run is posted there and never runs
        Task<OrganizeOperation> organize;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            organize = svc.OrganizeAsync(files, copyRequest, RunContext.NewAuto());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(svc.CompleteInFlightRuns(TimeSpan.FromSeconds(5)));

        var saved = Assert.Single(await HistoryService.LoadAsync());
        Assert.Equal(4, saved.Moves.Count);
        Assert.Single(svc.History);
        Assert.False(organize.IsCompleted);
        var reloaded = new CopyJournal();
        await reloaded.ReloadAsync();
        Assert.All(files, f => Assert.True(reloaded.Contains(dir, f)));

        // When the "UI thread" gets to the continuation later, it doesn't record the run twice
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!organize.IsCompleted && DateTime.UtcNow < deadline)
        {
            ui.RunQueued();
            await Task.Delay(10);
        }
        await organize;
        Assert.Single(svc.History);
        Assert.Single(await HistoryService.LoadAsync());
    }

    [Fact]
    public void CompleteInFlightRuns_NothingRunning_ReturnsAtOnce()
    {
        var svc = new FileOrganizerService();

        Assert.True(svc.CompleteInFlightRuns(TimeSpan.FromSeconds(5)));
        Assert.Empty(svc.History);
    }
}
