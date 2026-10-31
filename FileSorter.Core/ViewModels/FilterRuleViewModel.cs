using System.Windows.Input;
using FileSorter.Models;

namespace FileSorter.ViewModels;

/// <summary>Editor row for one rule condition; every property writes straight into <see cref="Model"/>.</summary>
public class FilterRuleViewModel : ObservableObject
{
    public FilterRule Model { get; }

    public RuleField Field
    {
        get => Model.Field;
        set
        {
            if (Model.Field == value) return;
            Model.Field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableOperators));
            Operator = AvailableOperators[0];
        }
    }

    public RuleOperator Operator
    {
        get => Model.Operator;
        set
        {
            if (Model.Operator == value) return;
            Model.Operator = value;
            OnPropertyChanged();
        }
    }

    public string Value
    {
        get => Model.Value;
        set
        {
            value ??= string.Empty;
            if (Model.Value == value) return;
            Model.Value = value;
            OnPropertyChanged();
        }
    }

    public Array AvailableFields => Enum.GetValues(typeof(RuleField));

    /// <summary>Only operators that can be evaluated for the field; dates support "last N days" only.</summary>
    public RuleOperator[] AvailableOperators => FilterRuleSnapshot.OperatorsFor(Field);

    public event Action? RemoveRequested;

    private readonly RelayCommand _removeCommand;
    public ICommand RemoveCommand => _removeCommand;

    /// <summary>Set by the owning rule: whether this condition may be removed right now.</summary>
    public Func<bool> CanRemove { get; set; } = () => true;

    public FilterRuleViewModel(FilterRule model)
    {
        Model = model;
        _removeCommand = new RelayCommand(_ => RemoveRequested?.Invoke(), _ => CanRemove());
    }

    public void RaiseCanRemoveChanged() => _removeCommand.RaiseCanExecuteChanged();
}
