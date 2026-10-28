using FileSorter.Models;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

/// <summary>A condition that can't be evaluated makes its rule inert instead of matching every file.</summary>
public class RuleConditionValidityTests
{
    private static RoutingRule Rule(RuleField field, RuleOperator op, string value) =>
        PlannerTests.Rule("Dest", field, op, value);

    private static FileItem Sized(long bytes) => TestData.Item(@"C:\src\a.bin", size: bytes);

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5.2")]
    [InlineData("-1")]
    [InlineData("NaN")]
    public void Size_UnparsableValue_RuleIsInert(string value)
    {
        Assert.Null(Rule(RuleField.Size, RuleOperator.GreaterThan, value).Snapshot());
        Assert.Null(Rule(RuleField.Size, RuleOperator.LessThan, value).Snapshot());
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData(" 1.5 ")]
    public void Size_FractionalMegabytes_Works(string value)
    {
        var snapshot = Rule(RuleField.Size, RuleOperator.GreaterThan, value).Snapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Matches(Sized(2 * 1024 * 1024)));
        Assert.False(snapshot.Matches(Sized(1024 * 1024)));
        Assert.True(Rule(RuleField.Size, RuleOperator.LessThan, value).Snapshot()!.Matches(Sized(1024 * 1024)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-3")]
    public void Date_UnparsableDays_RuleIsInert(string value)
    {
        Assert.Null(Rule(RuleField.DateModified, RuleOperator.LastDays, value).Snapshot());
        Assert.Null(Rule(RuleField.DateCreated, RuleOperator.LastDays, value).Snapshot());
    }

    [Theory]
    [InlineData(RuleOperator.GreaterThan)]
    [InlineData(RuleOperator.LessThan)]
    public void Date_GreaterOrLessThan_RuleIsInert(RuleOperator op)
    {
        Assert.Null(Rule(RuleField.DateModified, op, "5").Snapshot());
        Assert.Null(Rule(RuleField.DateCreated, op, "5").Snapshot());
    }

    [Theory]
    [InlineData(RuleField.Size, RuleOperator.Contains)]
    [InlineData(RuleField.Name, RuleOperator.GreaterThan)]
    [InlineData(RuleField.Extension, RuleOperator.LastDays)]
    public void UnsupportedFieldOperatorPair_RuleIsInert(RuleField field, RuleOperator op)
    {
        Assert.Null(Rule(field, op, "5").Snapshot());
    }

    [Fact]
    public void Date_LastDays_Works()
    {
        var snapshot = Rule(RuleField.DateModified, RuleOperator.LastDays, "7").Snapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Matches(TestData.Item(@"C:\src\new.txt", DateTime.Now.AddDays(-1))));
        Assert.False(snapshot.Matches(TestData.Item(@"C:\src\old.txt", DateTime.Now.AddDays(-30))));
    }

    [Fact]
    public void DateFields_OfferOnlyLastDays()
    {
        var vm = new FilterRuleViewModel(new FilterRule()) { Field = RuleField.DateModified };

        Assert.Equal([RuleOperator.LastDays], vm.AvailableOperators);
        Assert.Equal(RuleOperator.LastDays, vm.Operator);
    }

    [Fact]
    public void InvalidCondition_KeepsRulesOff_AndPlanUntouched()
    {
        var profile = FolderProfile.CreateDefault(@"C:\src");
        profile.Rules.Add(Rule(RuleField.Size, RuleOperator.GreaterThan, "abc"));
        var request = Services.OrganizeRequest.FromProfile(profile, Services.FileCategoryService.GetDefaultCategories());

        Assert.Empty(request.Rules);
        var group = Assert.Single(Services.FileOrganizerService.Plan([Sized(10)], request));
        Assert.NotEqual(Path.Combine(@"C:\src", "Dest"), group.FolderPath);
    }
}
