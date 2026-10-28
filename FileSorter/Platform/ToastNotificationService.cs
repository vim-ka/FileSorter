using System.Windows.Threading;
using FileSorter.Abstractions;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FileSorter.Platform;

/// <summary>
/// Windows toast notifications with an "Undo" button. Undo callbacks are kept by toast id; a click
/// arrives on a background thread and is handed to the UI thread. A click this process has no
/// callback for (a toast from an earlier run) just opens the window.
/// </summary>
public sealed class ToastNotificationService : INotificationService, IDisposable
{
    private const string ActionKey = "action";
    private const string IdKey = "id";
    private const string UndoAction = "undo";
    private const string OpenAction = "open";
    /// <summary>Toasts left unclicked keep their callback; the oldest are forgotten beyond this.</summary>
    private const int MaxCallbacks = 100;

    private readonly Dispatcher _dispatcher;
    private readonly Action _onOpen;
    private readonly object _lock = new();
    private readonly Dictionary<string, Action> _callbacks = [];
    private readonly Queue<string> _order = new();
    private bool _disposed;

    /// <param name="onOpen">Shows the main window; invoked on the UI thread.</param>
    public ToastNotificationService(Dispatcher dispatcher, Action onOpen)
    {
        _dispatcher = dispatcher;
        _onOpen = onOpen;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <summary>The process was started by a click on a toast (after the program had exited).</summary>
    public static bool WasProcessToastActivated()
    {
        try
        {
            return ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void ShowAutoOrganized(string title, string message, Action onUndo)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            if (_disposed) return;
            _callbacks[id] = onUndo;
            _order.Enqueue(id);
            while (_order.Count > MaxCallbacks)
                _callbacks.Remove(_order.Dequeue());
        }

        try
        {
            new ToastContentBuilder()
                .AddArgument(ActionKey, OpenAction)
                .AddArgument(IdKey, id)
                .AddText(title)
                .AddText(message)
                .AddButton(new ToastButton()
                    .SetContent("Undo")
                    .AddArgument(ActionKey, UndoAction)
                    .AddArgument(IdKey, id))
                .Show();
        }
        catch (Exception)
        {
            // Notifications unavailable (disabled, no notification platform): the history still has the run
            lock (_lock) _callbacks.Remove(id);
        }
    }

    /// <summary>Background thread; also raised for a toast clicked while the program was not running.</summary>
    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            Action? undo = null;
            if (args.TryGetValue(ActionKey, out var action) && action == UndoAction
                && args.TryGetValue(IdKey, out var id))
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_callbacks.Remove(id, out var callback))
                        undo = callback;
                }
            }

            _dispatcher.InvokeAsync(undo ?? _onOpen);
        }
        catch (Exception)
        {
            // Malformed arguments or the dispatcher shutting down: ignore the click
        }
    }

    /// <summary>On exit: stop listening and forget the callbacks (the view model is gone).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _callbacks.Clear();
            _order.Clear();
        }
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
    }
}
