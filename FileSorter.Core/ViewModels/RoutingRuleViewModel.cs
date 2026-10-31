using System.Collections.ObjectModel;
using System.Windows.Input;
using FileSorter.Models;

namespace FileSorter.ViewModels;

/// <summary>
/// Editor card for one routing rule. Every edit (including its conditions) writes straight into
/// <see cref="Model"/> and raises <see cref="Changed"/>.
/// </summary>
public class RoutingRuleViewModel : ObservableObject
{
    public RoutingRule Model { get; }

    public ObservableCollection<FilterRuleViewModel> Conditions { get; } = [];

    public ICommand AddConditionCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }

    /// <summary>Raised after any edit of the rule or one of its conditions.</summary>
    public event Action? Changed;

    public RoutingRuleViewModel(RoutingRule model)
    {
        Model = model;
        foreach (var condition in model.Conditions)
            Conditions.Add(CreateCondition(condition));
        AddConditionCommand = new RelayCommand(_ => AddCondition());
        AddFolderCommand = new RelayCommand(_ => SetFolderShown(true));
        RemoveFolderCommand = new RelayCommand(_ =>
        {
            Destination = "";
            SetFolderShown(false);
        });
        // The last condition can't be removed (see CreateCondition): refresh the remove buttons
        Conditions.CollectionChanged += (_, _) =>
        {
            foreach (var c in Conditions)
                c.RaiseCanRemoveChanged();
        };
    }

    public bool IsEnabled
    {
        get => Model.IsEnabled;
        set
        {
            if (Model.IsEnabled == value) return;
            Model.IsEnabled = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    public bool MatchAll
    {
        get => Model.MatchAll;
        set
        {
            if (Model.MatchAll == value) return;
            Model.MatchAll = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MatchAny));
            Changed?.Invoke();
        }
    }

    /// <summary>Inverse of <see cref="MatchAll"/>, for the All/Any radio pair.</summary>
    public bool MatchAny
    {
        get => !MatchAll;
        set => MatchAll = !value;
    }

    public string Destination
    {
        get => Model.Destination;
        set
        {
            value ??= "";
            if (Model.Destination == value) return;
            Model.Destination = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFolder));
            Changed?.Invoke();
        }
    }

    private bool _folderShown;

    /// <summary>
    /// Whether the card shows a folder field. Without one, matching files go to the destination
    /// folder itself (an empty <see cref="Destination"/>); "+ folder" adds the field.
    /// </summary>
    public bool HasFolder => _folderShown || !string.IsNullOrWhiteSpace(Destination);

    private void SetFolderShown(bool shown)
    {
        if (_folderShown == shown) return;
        _folderShown = shown;
        OnPropertyChanged(nameof(HasFolder));
    }

    private void AddCondition()
    {
        var model = new FilterRule();
        Model.Conditions.Add(model);
        Conditions.Add(CreateCondition(model));
        Changed?.Invoke();
    }

    private FilterRuleViewModel CreateCondition(FilterRule model)
    {
        // A rule without conditions matches every file: removing the last condition would silently
        // turn the rule into a catch-all, so it can't be removed (the rule itself can)
        var vm = new FilterRuleViewModel(model) { CanRemove = () => Conditions.Count > 1 };
        vm.RemoveRequested += () =>
        {
            if (Conditions.Count <= 1) return;
            Model.Conditions.Remove(model);
            Conditions.Remove(vm);
            Changed?.Invoke();
        };
        vm.PropertyChanged += (_, e) =>
        {
            // AvailableOperators is derived; Field/Operator/Value carry the actual edits
            if (e.PropertyName != nameof(FilterRuleViewModel.AvailableOperators))
                Changed?.Invoke();
        };
        return vm;
    }
}
