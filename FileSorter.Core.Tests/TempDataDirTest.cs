using FileSorter.Services;

namespace FileSorter.Core.Tests;

/// <summary>Base class: redirects JsonFileStore.DataDir to a fresh temp folder for each test.</summary>
public abstract class TempDataDirTest : IDisposable
{
    private readonly string _previousDataDir;

    protected string TempDir { get; }

    protected TempDataDirTest()
    {
        TempDir = TestData.NewTempDir("data");
        _previousDataDir = JsonFileStore.DataDir;
        JsonFileStore.DataDir = TempDir;
    }

    /// <summary>Runs first in <see cref="Dispose"/>, while the temp data folder is still in use.</summary>
    protected virtual void BeforeDispose() { }

    public void Dispose()
    {
        BeforeDispose();
        JsonFileStore.DataDir = _previousDataDir;
        try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
