using System.Windows.Threading;
using FileSorter.Abstractions;

namespace FileSorter.Platform;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;
}
