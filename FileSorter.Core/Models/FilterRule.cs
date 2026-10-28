using System.Globalization;

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

/// <summary>One condition of a routing rule.</summary>
public class FilterRule
{
    public RuleField Field { get; set; } = RuleField.Name;
    public RuleOperator Operator { get; set; } = RuleOperator.Contains;
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// The operator is supported for the field and the value is usable (not blank; a whole number
    /// of days for dates, megabytes for size). A rule with a condition that isn't is inert.
    /// </summary>
    public bool IsEvaluable => FilterRuleSnapshot.IsEvaluable(Field, Operator, Value);

    public FilterRuleSnapshot Snapshot() => new(Field, Operator, Value);
}

public record FilterRuleSnapshot(RuleField Field, RuleOperator Operator, string Value)
{
    /// <summary>Operators that can be evaluated for the field (the ones the editor offers).</summary>
    public static RuleOperator[] OperatorsFor(RuleField field) => field switch
    {
        RuleField.DateCreated or RuleField.DateModified => [RuleOperator.LastDays],
        RuleField.Size => [RuleOperator.GreaterThan, RuleOperator.LessThan],
        _ => [RuleOperator.Contains, RuleOperator.Is, RuleOperator.StartsWith, RuleOperator.EndsWith]
    };

    public static bool IsEvaluable(RuleField field, RuleOperator op, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !OperatorsFor(field).Contains(op)) return false;
        return field switch
        {
            RuleField.DateCreated or RuleField.DateModified => TryParseDays(value, out _),
            RuleField.Size => TryParseMegabytes(value, out _),
            _ => true
        };
    }

    /// <summary>A whole, non-negative number of days.</summary>
    public static bool TryParseDays(string value, out int days) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out days) && days >= 0;

    /// <summary>Non-negative megabytes, "1.5" or "1,5" in any UI culture.</summary>
    public static bool TryParseMegabytes(string value, out double megabytes) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite
                | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out megabytes)
        && double.IsFinite(megabytes);

    /// <summary>A condition that can't be evaluated never matches (its rule is inert anyway).</summary>
    public bool Matches(FileItem file)
    {
        if (!IsEvaluable(Field, Operator, Value)) return false;
        return Field switch
        {
            RuleField.Name => MatchString(file.FileName),
            RuleField.Extension => MatchString(file.Extension.TrimStart('.')),
            RuleField.Content => MatchString(file.FileName),
            RuleField.DateCreated => MatchDate(file.DateCreated),
            RuleField.DateModified => MatchDate(file.DateModified),
            RuleField.Size => MatchSize(file.Size),
            _ => false
        };
    }

    private bool MatchString(string target)
    {
        var val = Value.ToLowerInvariant();
        var tgt = target.ToLowerInvariant();
        return Operator switch
        {
            RuleOperator.Contains => tgt.Contains(val),
            RuleOperator.Is => tgt == val,
            RuleOperator.StartsWith => tgt.StartsWith(val),
            RuleOperator.EndsWith => tgt.EndsWith(val),
            _ => false
        };
    }

    private bool MatchDate(DateTime date)
    {
        TryParseDays(Value, out var days);
        return Operator == RuleOperator.LastDays && date >= DateTime.Now.AddDays(-days);
    }

    private bool MatchSize(long size)
    {
        TryParseMegabytes(Value, out var megabytes);
        var bytes = megabytes * 1024 * 1024;
        return Operator switch
        {
            RuleOperator.GreaterThan => size > bytes,
            RuleOperator.LessThan => size < bytes,
            _ => false
        };
    }
}
