using System.Windows.Input;
using FileSorter.Models;

namespace FileSorter.ViewModels;

public class FilterRuleViewModel : ObservableObject
{
    private RuleField _field = RuleField.Name;
    private RuleOperator _operator = RuleOperator.Contains;
    private string _value = string.Empty;

    public RuleField Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                OnPropertyChanged(nameof(AvailableOperators));
                Operator = AvailableOperators[0];
            }
        }
    }

    public RuleOperator Operator
    {
        get => _operator;
        set => SetProperty(ref _operator, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public Array AvailableFields => Enum.GetValues(typeof(RuleField));

    public RuleOperator[] AvailableOperators => Field switch
    {
        RuleField.DateCreated or RuleField.DateModified => [RuleOperator.LastDays, RuleOperator.GreaterThan, RuleOperator.LessThan],
        RuleField.Size => [RuleOperator.GreaterThan, RuleOperator.LessThan],
        _ => [RuleOperator.Contains, RuleOperator.Is, RuleOperator.StartsWith, RuleOperator.EndsWith]
    };

    public event Action? RemoveRequested;
    public ICommand RemoveCommand { get; }

    public FilterRuleViewModel()
    {
        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke());
    }
}
