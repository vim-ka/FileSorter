namespace FileSorter.Models;

public enum RuleField
{
    Name,
    Extension,
    Content,
    DateCreated,
    DateModified,
    Size
}

public enum RuleOperator
{
    Contains,
    Is,
    StartsWith,
    EndsWith,
    LastDays,
    GreaterThan,
    LessThan
}

public class FilterRule
{
    public RuleField Field { get; set; } = RuleField.Name;
    public RuleOperator Operator { get; set; } = RuleOperator.Contains;
    public string Value { get; set; } = string.Empty;

    public FilterRuleSnapshot Snapshot() => new(Field, Operator, Value);
}

public record FilterRuleSnapshot(RuleField Field, RuleOperator Operator, string Value)
{
    public bool Matches(FileItem file) => Field switch
    {
        RuleField.Name => MatchString(file.FileName),
        RuleField.Extension => MatchString(file.Extension.TrimStart('.')),
        RuleField.Content => MatchString(file.FileName),
        RuleField.DateCreated => MatchDate(file.DateCreated),
        RuleField.DateModified => MatchDate(file.DateModified),
        RuleField.Size => MatchSize(file.Size),
        _ => true
    };

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
        var bytes = val * 1024 * 1024;
        return Operator switch
        {
            RuleOperator.GreaterThan => size > bytes,
            RuleOperator.LessThan => size < bytes,
            _ => true
        };
    }
}
