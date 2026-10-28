using System.Runtime.InteropServices;

namespace FileSorter.Platform;

/// <summary>
/// One running copy per user session: a named mutex marks the first instance, a named event lets a
/// later launch ask it to show its window.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "FileSorter.SingleInstance";
    private const string ActivateEventName = "FileSorter.SingleInstance.Activate";
    private const int AsfwAny = -1;

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static RegisteredWaitHandle? _activateWait;

    /// <summary>
    /// True for the first instance: <paramref name="onActivateRequested"/> is then invoked (on a thread
    /// pool thread) each time another launch asks for the window. False for a later launch, which has
    /// already signalled the first instance and should exit.
    /// </summary>
    public static bool TryAcquire(Action onActivateRequested)
    {
        // Created (or opened) before the mutex, so a launch that loses the race always finds it;
        // an auto-reset event stays signalled until the first instance starts waiting
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            // Let the first instance bring its window to the foreground (this launch owns the focus)
            AllowSetForegroundWindow(AsfwAny);
            activateEvent.Set();
            activateEvent.Dispose();
            return false;
        }

        _mutex = mutex;
        _activateEvent = activateEvent;
        _activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent,
            (_, _) => onActivateRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    /// <summary>
    /// Releases the instance on exit, so a launch during the remaining shutdown starts a new copy
    /// instead of signalling this one. Call on the thread that called <see cref="TryAcquire"/>.
    /// </summary>
    public static void Release()
    {
        _activateWait?.Unregister(null);
        _activateWait = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
        if (_mutex == null) return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; closing the handle below is enough
        }
        _mutex.Dispose();
        _mutex = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
