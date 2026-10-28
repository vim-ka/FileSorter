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

    /// <summary>Raised after any edit of the rule or one of its conditions.</summary>
    public event Action? Changed;

    public RoutingRuleViewModel(RoutingRule model)
    {
        Model = model;
        foreach (var condition in model.Conditions)
            Conditions.Add(CreateCondition(condition));
        AddConditionCommand = new RelayCommand(_ => AddCondition());
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
            Changed?.Invoke();
        }
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
        var vm = new FilterRuleViewModel(model);
        vm.RemoveRequested += () =>
        {
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
