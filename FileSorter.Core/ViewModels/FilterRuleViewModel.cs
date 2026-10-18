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

    public FilterRule ToModel() => new()
    {
        Field = Field,
        Operator = Operator,
        Value = Value
    };

    public bool Matches(FileItem file)
    {
        return Field switch
        {
            RuleField.Name => MatchString(file.FileName),
            RuleField.Extension => MatchString(file.Extension.TrimStart('.')),
            RuleField.Content => MatchString(file.FileName),
            RuleField.DateCreated => MatchDate(file.DateCreated),
            RuleField.DateModified => MatchDate(file.DateModified),
            RuleField.Size => MatchSize(file.Size),
            _ => true
        };
    }

    private bool MatchString(string target)
    {
        if (string.IsNullOrEmpty(Value)) return true;
        var val = Value.ToLowerInvariant();
        var tgt = target.ToLowerInvariant();
        return Operator switch
        {
            RuleOperator.Contains => tgt.Contains(val),
            RuleOperator.Is => tgt == val,
            RuleOperator.StartsWith => tgt.StartsWith(val),
            RuleOperator.EndsWith => tgt.EndsWith(val),
            _ => true
        };
    }

    private bool MatchDate(DateTime date)
    {
        if (!int.TryParse(Value, out var days)) return true;
        return Operator switch
        {
            RuleOperator.LastDays => date >= DateTime.Now.AddDays(-days),
            _ => true
        };
    }

    private bool MatchSize(long size)
    {
        if (!long.TryParse(Value, out var val)) return true;
        var bytes = val * 1024 * 1024; // Value in MB
        return Operator switch
        {
            RuleOperator.GreaterThan => size > bytes,
            RuleOperator.LessThan => size < bytes,
            _ => true
        };
    }
}
