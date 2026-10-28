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
