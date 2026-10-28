using FileSorter.Abstractions;

namespace FileSorter.Core.Tests;

/// <summary>Runs UI actions immediately (no UI thread in tests).</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

/// <summary>Folder picker answers come from <see cref="Folders"/>; an empty queue means "cancelled".</summary>
internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> Folders { get; } = new();
    public List<string> Errors { get; } = [];

    public string? PickFolder(string title) => Folders.Count > 0 ? Folders.Dequeue() : null;
    public void ShowError(string title, string message) => Errors.Add(message);
}

internal sealed class NullThumbnails : IThumbnailProvider
{
    public object? GetOrRequest(string path, DateTime dateModified, int size, Action<object> onLoaded) => null;
    public void CancelPending() { }
}

internal sealed class NullTheme : IThemeService
{
    public void ApplyTheme(bool isDark) { }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for condition");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// A UI thread stand-in: one dedicated thread with its own SynchronizationContext, so awaits
/// started on it resume on it (like WPF's dispatcher). Use <see cref="RunAsync{T}"/> to call into
/// code that must live on the UI thread.
/// </summary>
internal sealed class SingleThreadDispatcher : IUiDispatcher, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadDispatcher()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "TestUiThread" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public bool IsOnThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action) => InvokeAsync(action);

    public Task<T> RunAsync<T>(Func<T> func) => RunAsync(() => Task.FromResult(func()));

    /// <summary>Starts <paramref name="func"/> on the thread; its awaits resume there too.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            Task<T> task;
            try { task = func(); }
            catch (Exception e) { tcs.SetException(e); return; }
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) tcs.SetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.SetCanceled();
                else tcs.SetResult(t.Result);
            }, TaskScheduler.Default);
        });
        return tcs.Task;
    }

    private void Post(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { } // disposed — drop, like a shut-down dispatcher
    }

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsOnThread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class Context(SingleThreadDispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (owner.IsOnThread) d(state);
            else owner.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
