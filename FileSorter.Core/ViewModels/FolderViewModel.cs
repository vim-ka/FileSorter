using FileSorter.Models;

namespace FileSorter.ViewModels;

public enum AutoStatus
{
    Off,
    On,
    Paused
}

/// <summary>A row of the managed folder list; wraps the folder's persisted profile.</summary>
public class FolderViewModel : ObservableObject
{
    private bool _isChecked;
    private bool _isSelected;
    private AutoStatus _autoStatus;

    public FolderViewModel(FolderProfile profile)
    {
        Profile = profile;
    }

    public FolderProfile Profile { get; }

    public string Name => Profile.Name;
    public string FullPath => Profile.FullPath;

    public int FileCount
    {
        get => Profile.FileCount;
        set
        {
            if (Profile.FileCount == value) return;
            Profile.FileCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Marked for a multi-folder organize run.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>The folder whose profile is currently shown in the UI.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public AutoStatus AutoStatus
    {
        get => _autoStatus;
        private set => SetProperty(ref _autoStatus, value);
    }

    public void RefreshStatus(bool pauseAll)
    {
        var auto = Profile.Auto;
        AutoStatus = !auto.Enabled ? AutoStatus.Off
            : auto.IsPaused || pauseAll ? AutoStatus.Paused
            : AutoStatus.On;
    }
}
