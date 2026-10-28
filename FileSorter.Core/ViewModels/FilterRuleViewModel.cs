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
    public ICommand RemoveCommand { get; }

    public FilterRuleViewModel(FilterRule model)
    {
        Model = model;
        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke());
    }
}
