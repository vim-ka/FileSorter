using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

/// <summary>
/// Drives the engine on a dedicated "UI" thread with its own SynchronizationContext, so the
/// engine's state is only ever touched on that thread — as in the app.
/// </summary>
public sealed class AutoOrganizeEngineTests : TempDataDirTest, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly SingleThreadDispatcher _ui = new();
    private readonly CopyJournal _journal = new();
    private readonly FileOrganizerService _organizer;
    private readonly FolderGate _gate = new();
    private readonly AutoOrganizeEngine _engine;
    private readonly List<(string Folder, OrganizeOperation Op)> _runs = [];
    private readonly List<bool> _runsOnUiThread = [];

    public AutoOrganizeEngineTests()
    {
        _organizer = new FileOrganizerService(_journal);
        _engine = CreateEngine(new AutoOrganizeOptions(Debounce, TimeSpan.Zero, Debounce), null);
    }

    private AutoOrganizeEngine CreateEngine(AutoOrganizeOptions options, Func<AutoOrganizeSettings, TimeSpan>? schedule)
    {
        var engine = new AutoOrganizeEngine(_organizer, _journal, _gate, _ui, options, schedule);
        engine.RunCompleted += (folder, op) =>
        {
            lock (_runs)
            {
                _runs.Add((folder, op));
                _runsOnUiThread.Add(_ui.IsOnThread);
            }
        };
        return engine;
    }

    public new void Dispose()
    {
        _ui.RunAsync(() => _engine.Dispose()).Wait(TimeSpan.FromSeconds(5));
        _ui.Dispose();
        base.Dispose();
    }

    private int RunCount { get { lock (_runs) return _runs.Count; } }

    private List<(string Folder, OrganizeOperation Op)> Runs { get { lock (_runs) return [.. _runs]; } }

    private string NewFolder(string name)
    {
        var dir = Path.Combine(TempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FolderProfile AutoProfile(string dir, bool onNewFiles = true)
    {
        var p = FolderProfile.CreateDefault(dir);
        p.Auto.Enabled = true;
        p.Auto.OnNewFiles = onNewFiles;
        return p;
    }

    /// <summary>Configures with an empty list first, so the given profiles don't get a startup run.</summary>
    private Task ConfigureWithoutStartupRun(params FolderProfile[] profiles) => _ui.RunAsync(() =>
    {
        _engine.Configure([], false);
        _engine.Configure(profiles, false);
    });

    private Task<OrganizeOperation?> RunNow(string folder) => _ui.RunAsync(() => _engine.RunNowAsync(folder));

    private static Task Quiet() => Task.Delay(Debounce * 3);

    [Fact]
    public async Task NotifyChanged_Debounced_SingleRun()
    {
        var dir = NewFolder("A");
        await ConfigureWithoutStartupRun(AutoProfile(dir));

        for (int i = 0; i < 5; i++)
        {
            TestData.WriteFile(dir, $"f{i}.txt");
            await _ui.RunAsync(() => _engine.NotifyChanged(dir));
            await Task.Delay(20);
        }

        await Wait.UntilAsync(() => RunCount > 0);
        await Quiet();

        var run = Assert.Single(Runs);
        Assert.Equal(5, run.Op.Moves.Count);
        Assert.All(_runsOnUiThread, Assert.True);
    }

    [Fact]
    public async Task Run_OnlyRootFiles_SubfoldersUntouched()
    {
        var dir = NewFolder("A");
        var profile = AutoProfile(dir);
        profile.IncludeSubfolders = true;
        TestData.WriteFile(dir, "a.txt");
        var nested = TestData.WriteFile(Directory.CreateDirectory(Path.Combine(dir, "Inbox")).FullName, "b.txt");
        await ConfigureWithoutStartupRun(profile);

        var op = await RunNow(dir);

        Assert.NotNull(op);
        var move = Assert.Single(op.Moves);
        Assert.Equal(Path.Combine(dir, "a.txt"), move.SourcePath);
        Assert.True(File.Exists(Path.Combine(dir, "Documents", "a.txt")));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task Run_IsAuto_OperationInHistory()
    {
        var dir = NewFolder("A");
        TestData.WriteFile(dir, "a.txt");
        await ConfigureWithoutStartupRun(AutoProfile(dir));

        var op = await RunNow(dir);

        Assert.NotNull(op);
        Assert.True(op.IsAuto);
        Assert.NotEqual(Guid.Empty, op.RunId);
        var history = await _ui.RunAsync(() => _organizer.History.ToList());
        Assert.Same(op, Assert.Single(history));
        var completed = Assert.Single(Runs);
        Assert.Same(op, completed.Op);
        Assert.Equal(dir, completed.Folder);
    }

    [Fact]
    public async Task Run_GateBusy_Deferred()
    {
        var dir = NewFolder("A");
        var file = TestData.WriteFile(dir, "a.txt");
        await ConfigureWithoutStartupRun(AutoProfile(dir));
        Assert.True(_gate.TryEnter(dir));

        await _ui.RunAsync(() => _engine.NotifyChanged(dir));
        Assert.Null(await RunNow(dir));
        await Quiet();

        Assert.Equal(0, RunCount);
        Assert.True(File.Exists(file));

        _gate.Exit(dir);
        await _ui.RunAsync(() => _engine.NotifyChanged(dir));
        await Wait.UntilAsync(() => RunCount == 1);
        Assert.False(File.Exists(file));
        Assert.False(_gate.IsBusy(dir));
    }

    [Fact]
    public async Task Run_CopyMode_SecondRunCopiesNothing()
    {
        var dir = NewFolder("A");
        var profile = AutoProfile(dir);
        profile.CopyMode = true;
        var file = TestData.WriteFile(dir, "a.txt");
        await ConfigureWithoutStartupRun(profile);

        var first = await RunNow(dir);
        var second = await RunNow(dir);

        Assert.NotNull(first);
        Assert.Single(first.Moves);
        Assert.True(second == null || second.Moves.Count == 0);
        Assert.Single(Runs);
        Assert.True(File.Exists(file));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "Documents")));
    }

    [Fact]
    public async Task Run_CopyMode_WaitsForDelayedJournalLoad_DoesNotRecopy()
    {
        var dir = NewFolder("A");
        var file = TestData.WriteFile(dir, "a.txt");
        var info = new FileInfo(file);
        var loadGate = new TaskCompletionSource<List<CopyJournal.Entry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new CopyJournal(() => loadGate.Task);
        var engine = new AutoOrganizeEngine(new FileOrganizerService(journal), journal, new FolderGate(), _ui,
            new AutoOrganizeOptions(Debounce, TimeSpan.Zero, Debounce));
        var profile = AutoProfile(dir, onNewFiles: false);
        profile.CopyMode = true;
        try
        {
            // As at startup: the journal load is still running when the first auto run starts
            var load = journal.ReloadAsync();
            await _ui.RunAsync(() => engine.Configure([profile], false));
            await Quiet();
            Assert.False(Directory.Exists(Path.Combine(dir, "Documents")));

            loadGate.SetResult(
            [
                new CopyJournal.Entry
                {
                    SourceFolder = dir, Path = file, Size = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc
                }
            ]);
            await load;
            var op = await _ui.RunAsync(() => engine.RunNowAsync(dir));
            await Quiet();

            Assert.True(op == null || op.Moves.Count == 0);
            Assert.False(Directory.Exists(Path.Combine(dir, "Documents")));
            Assert.True(journal.Contains(dir, TestData.ItemFromDisk(file))); // not pruned by the early run
        }
        finally
        {
            await _ui.RunAsync(() => engine.Dispose());
        }
    }

    [Fact]
    public async Task Defer_DuringPendingDebounce_PostponesTheRun()
    {
        var debounce = TimeSpan.FromMilliseconds(600);
        var dir = NewFolder("A");
        using var engine = new EngineWithOptions(this, new AutoOrganizeOptions(debounce, TimeSpan.Zero, debounce));
        var profile = AutoProfile(dir, onNewFiles: false);
        await _ui.RunAsync(() => { engine.Engine.Configure([], false); engine.Engine.Configure([profile], false); });
        var file = TestData.WriteFile(dir, "a.txt");

        await _ui.RunAsync(() => engine.Engine.NotifyChanged(dir)); // due at ~600 ms
        await Task.Delay(400);
        await _ui.RunAsync(() => engine.Engine.Defer(dir));         // now due at ~1000 ms
        await Task.Delay(400);                                       // ~800 ms: past the original due time

        Assert.Equal(0, RunCount);
        Assert.True(File.Exists(file));

        await Wait.UntilAsync(() => RunCount == 1);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Defer_ScheduledRunDueSoon_WaitsForQuiet()
    {
        var dir = NewFolder("A");
        var file = TestData.WriteFile(dir, "a.txt");
        var debounce = TimeSpan.FromMilliseconds(800);
        var engine = new AutoOrganizeEngine(_organizer, _journal, _gate, _ui,
            new AutoOrganizeOptions(debounce, TimeSpan.Zero, debounce), _ => TimeSpan.FromMilliseconds(300));
        var runs = 0;
        engine.RunCompleted += (_, _) => Interlocked.Increment(ref runs);
        var profile = AutoProfile(dir, onNewFiles: false);
        profile.Auto.ScheduleEnabled = true;
        try
        {
            await _ui.RunAsync(() =>
            {
                engine.Configure([], false);
                engine.Configure([profile], false); // first tick at ~300 ms
                engine.Defer(dir);                  // edits until ~800 ms
            });
            await Task.Delay(600);

            Assert.Equal(0, Volatile.Read(ref runs));
            await Wait.UntilAsync(() => Volatile.Read(ref runs) == 1);
            Assert.False(File.Exists(file));
        }
        finally
        {
            await _ui.RunAsync(() => engine.Dispose());
        }
    }

    [Fact]
    public async Task Run_FilesItPlacedInWatchedRoot_Ignored()
    {
        var a = NewFolder("A");
        var b = NewFolder("B");
        var profileA = AutoProfile(a);
        profileA.Rules.Add(new RoutingRule { Destination = b });
        var profileB = AutoProfile(b);
        TestData.WriteFile(a, "a.txt");
        await ConfigureWithoutStartupRun(profileA, profileB);

        var opA = await RunNow(a);
        var placed = Path.Combine(b, "a.txt");
        Assert.NotNull(opA);
        Assert.True(File.Exists(placed));

        var opB = await RunNow(b);
        await Quiet(); // B's watcher saw the new file — its debounced run must ignore it too

        Assert.True(opB == null || opB.Moves.Count == 0);
        Assert.True(File.Exists(placed));
        Assert.Equal([a], Runs.Select(r => r.Folder));

        // Once the file changes it is a new file for B (B's watcher picks the change up)
        File.AppendAllText(placed, " changed");
        await Wait.UntilAsync(() => Runs.Any(r => r.Folder == b));
        Assert.False(File.Exists(placed));
        Assert.Equal([a, b], Runs.Select(r => r.Folder));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Configure_PausedOrPauseAll_NoStartupRun(bool folderPaused, bool pauseAll)
    {
        var dir = NewFolder("A");
        var file = TestData.WriteFile(dir, "a.txt");
        var profile = AutoProfile(dir);
        profile.Auto.IsPaused = folderPaused;

        await _ui.RunAsync(() => _engine.Configure([profile], pauseAll));
        await Quiet();

        Assert.Equal(0, RunCount);
        Assert.True(File.Exists(file));
        Assert.Null(await RunNow(dir));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Configure_Enabled_StartupRunOnce()
    {
        var dir = NewFolder("A");
        TestData.WriteFile(dir, "a.txt");
        var profile = AutoProfile(dir, onNewFiles: false);
        var disabled = FolderProfile.CreateDefault(NewFolder("Manual"));
        var manualFile = TestData.WriteFile(disabled.FullPath, "m.txt");

        await _ui.RunAsync(() => _engine.Configure([profile, disabled], false));
        await Wait.UntilAsync(() => RunCount == 1);

        var later = TestData.WriteFile(dir, "b.txt");
        await _ui.RunAsync(() => _engine.Configure([profile, disabled], false));
        await Quiet();

        Assert.Single(Runs);
        Assert.True(File.Exists(later));
        Assert.True(File.Exists(manualFile));
    }

    [Fact]
    public async Task Schedule_RunsEveryInterval()
    {
        using var test = new ScheduledEngine(TempDir);
        await test.Ui.RunAsync(() => test.Engine.Configure([test.Profile], false));
        await Wait.UntilAsync(() => test.Count >= 1); // startup run

        var started = DateTime.UtcNow;
        for (int i = 2; i <= 3; i++)
        {
            TestData.WriteFile(test.Profile.FullPath, $"s{i}.txt");
            var expected = i;
            await Wait.UntilAsync(() => test.Count >= expected);
        }

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task Run_UnstableFile_RetriedAfterStability()
    {
        var dir = NewFolder("A");
        using var engine = new EngineWithOptions(this, new AutoOrganizeOptions(Debounce, TimeSpan.FromSeconds(1), Debounce));
        var profile = AutoProfile(dir, onNewFiles: false);
        await _ui.RunAsync(() => { engine.Engine.Configure([], false); engine.Engine.Configure([profile], false); });
        var file = TestData.WriteFile(dir, "fresh.txt");

        Assert.Null(await _ui.RunAsync(() => engine.Engine.RunNowAsync(dir)));
        Assert.True(File.Exists(file));

        await Wait.UntilAsync(() => RunCount == 1); // no further trigger needed
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task RunNow_MissingFolder_NoOperationNoThrow()
    {
        var missing = Path.Combine(TempDir, "Missing");
        await _ui.RunAsync(() => _engine.Configure([AutoProfile(missing)], false));

        var op = await RunNow(missing);
        await Quiet();

        Assert.Null(op);
        Assert.Empty(await _ui.RunAsync(() => _organizer.History.ToList()));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Dispose_PendingDebounce_NoRun()
    {
        var dir = NewFolder("A");
        var file = TestData.WriteFile(dir, "a.txt");
        await ConfigureWithoutStartupRun(AutoProfile(dir));

        await _ui.RunAsync(() => { _engine.NotifyChanged(dir); _engine.Dispose(); });
        await Quiet();

        Assert.Equal(0, RunCount);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Watcher_NewFile_OrganizedAfterDebounce()
    {
        var dir = NewFolder("A");
        await ConfigureWithoutStartupRun(AutoProfile(dir));

        var file = TestData.WriteFile(dir, "a.txt");

        await Wait.UntilAsync(() => RunCount == 1);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Watcher_FolderMissingAtStartup_RebuiltOnTheNextRun()
    {
        var dir = Path.Combine(TempDir, "Later");
        await ConfigureWithoutStartupRun(AutoProfile(dir)); // no folder, no watcher

        Directory.CreateDirectory(dir);
        Assert.Null(await RunNow(dir)); // nothing to do, but the watcher is set up now

        var file = TestData.WriteFile(dir, "a.txt");
        await Wait.UntilAsync(() => RunCount == 1);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Configure_Disabled_StopsWatching()
    {
        var dir = NewFolder("A");
        var profile = AutoProfile(dir);
        await ConfigureWithoutStartupRun(profile);
        profile.Auto.Enabled = false;
        await _ui.RunAsync(() => _engine.Configure([profile], false));

        var file = TestData.WriteFile(dir, "a.txt");
        await _ui.RunAsync(() => _engine.NotifyChanged(dir));
        await Quiet();

        Assert.Equal(0, RunCount);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(5, ScheduleUnit.Minutes, 5 * 60)]
    [InlineData(2, ScheduleUnit.Hours, 2 * 3600)]
    [InlineData(1, ScheduleUnit.Days, 86400)]
    [InlineData(0, ScheduleUnit.Hours, 3600)]
    [InlineData(-3, ScheduleUnit.Minutes, 60)]
    public void DefaultScheduleInterval_IntervalTimesUnit(int interval, ScheduleUnit unit, int expectedSeconds)
    {
        var settings = new AutoOrganizeSettings { ScheduleInterval = interval, ScheduleUnit = unit };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AutoOrganizeEngine.DefaultScheduleInterval(settings));
    }

    [Fact]
    public void Options_Default_AreThirtySeconds()
    {
        var d = AutoOrganizeOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(30), d.Debounce);
        Assert.Equal(TimeSpan.FromSeconds(30), d.Stability);
        Assert.Equal(TimeSpan.FromSeconds(30), d.NotificationInterval);
    }

    /// <summary>A second engine on the shared thread/organizer with custom options; RunCompleted feeds the test's list.</summary>
    private sealed class EngineWithOptions(AutoOrganizeEngineTests test, AutoOrganizeOptions options) : IDisposable
    {
        public AutoOrganizeEngine Engine { get; } = test.CreateEngine(options, null);

        public void Dispose() => test._ui.RunAsync(() => Engine.Dispose()).Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>An engine with a 300 ms schedule and no watcher, on its own thread.</summary>
    private sealed class ScheduledEngine : IDisposable
    {
        private int _count;

        public ScheduledEngine(string tempDir)
        {
            var dir = Path.Combine(tempDir, "S");
            Directory.CreateDirectory(dir);
            TestData.WriteFile(dir, "s1.txt");
            Profile = AutoProfile(dir, onNewFiles: false);
            Profile.Auto.ScheduleEnabled = true;
            var journal = new CopyJournal();
            Engine = new AutoOrganizeEngine(new FileOrganizerService(journal), journal, new FolderGate(), Ui,
                new AutoOrganizeOptions(Debounce, TimeSpan.Zero, Debounce), _ => TimeSpan.FromMilliseconds(300));
            Engine.RunCompleted += (_, _) => Interlocked.Increment(ref _count);
        }

        public SingleThreadDispatcher Ui { get; } = new();
        public AutoOrganizeEngine Engine { get; }
        public FolderProfile Profile { get; }
        public int Count => Volatile.Read(ref _count);

        public void Dispose()
        {
            Ui.RunAsync(() => Engine.Dispose()).Wait(TimeSpan.FromSeconds(5));
            Ui.Dispose();
        }
    }
}
