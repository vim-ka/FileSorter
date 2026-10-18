namespace FileSorter.Abstractions;

/// <summary>Marshals work onto the UI thread of the host platform.</summary>
public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}
