using FileSorter.Models;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class RulesEditorTests : TempDataDirTest
{
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel NewVm() =>
        new(new InlineDispatcher(), _dialogs, new NullThumbnails(), new NullTheme());

    private FolderViewModel AddFolder(MainViewModel vm, string path)
    {
        _dialogs.Folders.Enqueue(path);
        vm.AddFolderCommand.Execute(null);
        return vm.SelectedFolder!;
    }

    private static RoutingRuleViewModel AddRule(MainViewModel vm, string destination)
    {
        vm.AddRuleCommand.Execute(null);
        var rule = vm.Rules[^1];
        rule.Destination = destination;
        return rule;
    }

    /// <summary>Top-level folder nodes under the preview's "Destination →" root.</summary>
    private static List<string> PreviewFolders(MainViewModel vm) =>
        vm.PreviewItems.Count == 0 ? [] : vm.PreviewItems[0].Children.Select(c => c.Name).ToList();

    [Fact]
    public void AddRule_AppearsInProfile()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        Assert.Equal("", vm.RulesActiveText);

        vm.AddRuleCommand.Execute(null);

        var rule = Assert.Single(vm.Rules);
        Assert.Same(a.Profile.Rules.Single(), rule.Model);
        Assert.True(rule.IsEnabled);
        Assert.Equal("RULES ON", vm.RulesActiveText);

        rule.Destination = "Movies";
        rule.MatchAll = false;
        Assert.Equal("Movies", a.Profile.Rules[0].Destination);
        Assert.False(a.Profile.Rules[0].MatchAll);

        rule.IsEnabled = false;
        Assert.False(a.Profile.Rules[0].IsEnabled);
        Assert.Equal("", vm.RulesActiveText);
    }

    [Fact]
    public void Conditions_WriteIntoTheRuleModel()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var rule = AddRule(vm, "Movies");
        var conditionsBefore = rule.Conditions.Count;

        rule.AddConditionCommand.Execute(null);
        var condition = rule.Conditions[^1];
        condition.Field = RuleField.Extension;
        condition.Operator = RuleOperator.Is;
        condition.Value = "mp4";

        var model = a.Profile.Rules[0].Conditions[^1];
        Assert.Equal(conditionsBefore + 1, a.Profile.Rules[0].Conditions.Count);
        Assert.Equal(RuleField.Extension, model.Field);
        Assert.Equal(RuleOperator.Is, model.Operator);
        Assert.Equal("mp4", model.Value);

        condition.RemoveCommand.Execute(null);
        Assert.Equal(conditionsBefore, rule.Conditions.Count);
        Assert.DoesNotContain(model, a.Profile.Rules[0].Conditions);
    }

    [Fact]
    public void MoveRuleUp_ReordersProfileRules()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        AddRule(vm, "A");
        AddRule(vm, "B");
        var c = AddRule(vm, "C");

        vm.MoveRuleUpCommand.Execute(c);
        Assert.Equal(["A", "C", "B"], a.Profile.Rules.Select(r => r.Destination));
        Assert.Equal(["A", "C", "B"], vm.Rules.Select(r => r.Destination));

        vm.MoveRuleUpCommand.Execute(c);
        vm.MoveRuleUpCommand.Execute(c); // already first: no-op
        Assert.Equal(["C", "A", "B"], a.Profile.Rules.Select(r => r.Destination));

        vm.MoveRuleDownCommand.Execute(c);
        Assert.Equal(["A", "C", "B"], a.Profile.Rules.Select(r => r.Destination));
        Assert.Equal(["A", "C", "B"], vm.Rules.Select(r => r.Destination));
    }

    [Fact]
    public void RemoveRule_RemovesFromProfile()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var first = AddRule(vm, "A");
        AddRule(vm, "B");

        vm.RemoveRuleCommand.Execute(first);

        Assert.Equal(["B"], a.Profile.Rules.Select(r => r.Destination));
        Assert.Equal(["B"], vm.Rules.Select(r => r.Destination));
    }

    [Fact]
    public void BrowseRuleDestination_SetsPickedFolder()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var rule = AddRule(vm, "Old");
        var picked = TestData.NewTempDir("Picked");

        _dialogs.Folders.Enqueue(picked);
        vm.BrowseRuleDestinationCommand.Execute(rule);
        Assert.Equal(picked, a.Profile.Rules[0].Destination);

        vm.BrowseRuleDestinationCommand.Execute(rule); // cancelled: unchanged
        Assert.Equal(picked, rule.Destination);
    }

    [Fact]
    public void Unmatched_WritesIntoProfile()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        vm.Unmatched = UnmatchedFilesMode.LeaveInPlace;
        Assert.Equal(UnmatchedFilesMode.LeaveInPlace, b.Profile.Unmatched);

        vm.SelectFolderCommand.Execute(a);
        Assert.Equal(UnmatchedFilesMode.OrganizeNormally, vm.Unmatched);
        vm.SelectFolderCommand.Execute(b);
        Assert.Equal(UnmatchedFilesMode.LeaveInPlace, vm.Unmatched);
    }

    [Fact]
    public async Task EditingCondition_RefreshesPreview()
    {
        var dir = TestData.NewTempDir("Src");
        TestData.WriteFile(dir, "clip.mp4");
        TestData.WriteFile(dir, "notes.txt");
        var vm = NewVm();
        AddFolder(vm, dir);
        await Wait.UntilAsync(() => vm.StatusText.Contains("will be organized"));

        var rule = AddRule(vm, "Movies");
        foreach (var existing in rule.Conditions.ToList())
            existing.RemoveCommand.Execute(null);
        rule.AddConditionCommand.Execute(null);
        var condition = rule.Conditions.Single();
        condition.Field = RuleField.Extension;
        condition.Operator = RuleOperator.Is;
        condition.Value = "zip";
        await Wait.UntilAsync(() => !vm.IsScanning && PreviewFolders(vm).Count > 0
                                     && !PreviewFolders(vm).Contains("Movies"));

        condition.Value = "mp4";

        await Wait.UntilAsync(() => PreviewFolders(vm).Contains("Movies"));
    }

    [Fact]
    public void SwitchFolder_RebuildsRulesFromItsProfile()
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var ruleA = AddRule(vm, "Movies");
        var b = AddFolder(vm, TestData.NewTempDir("B"));

        Assert.Empty(vm.Rules);
        Assert.Equal("", vm.RulesActiveText);
        AddRule(vm, "Docs");
        AddRule(vm, "Music");

        vm.SelectFolderCommand.Execute(a);
        var rebuilt = Assert.Single(vm.Rules);
        Assert.Equal("Movies", rebuilt.Destination);
        Assert.Same(a.Profile.Rules[0], rebuilt.Model);
        Assert.Equal("RULES ON", vm.RulesActiveText);

        ruleA.Destination = "Stale"; // a view model of the previous rebuild must not matter
        rebuilt.Destination = "Films";
        Assert.Equal(["Docs", "Music"], b.Profile.Rules.Select(r => r.Destination));

        vm.SelectFolderCommand.Execute(b);
        Assert.Equal(["Docs", "Music"], vm.Rules.Select(r => r.Destination));
    }

    [Fact]
    public async Task RuleToFullPath_ShowsInPreview_OrganizesAndUndoes()
    {
        var src = TestData.NewTempDir("Src");
        var video = TestData.NewTempDir("Video");
        var clip = TestData.WriteFile(src, "clip.mp4");
        TestData.WriteFile(src, "notes.txt");
        var vm = NewVm();
        AddFolder(vm, src);
        await Wait.UntilAsync(() => vm.StatusText.Contains("will be organized"));

        var rule = AddRule(vm, video);
        foreach (var existing in rule.Conditions.ToList())
            existing.RemoveCommand.Execute(null);
        rule.AddConditionCommand.Execute(null);
        rule.Conditions[0].Field = RuleField.Extension;
        rule.Conditions[0].Operator = RuleOperator.Is;
        rule.Conditions[0].Value = "mp4";

        // the folder outside the target is its own top-level node, named by its full path
        await Wait.UntilAsync(() => PreviewFolders(vm).Contains(video));
        var node = vm.PreviewItems[0].Children.Single(c => c.Name == video);
        Assert.Equal(["clip.mp4"], node.Children.Select(c => c.Name));

        vm.OrganizeCommand.Execute(null);
        var moved = Path.Combine(video, "clip.mp4");
        await Wait.UntilAsync(() => !vm.IsOrganizing && !vm.IsScanning && File.Exists(moved));
        Assert.False(File.Exists(clip));

        vm.UndoCommand.Execute(null);
        await Wait.UntilAsync(() => !vm.IsOrganizing && File.Exists(clip));
        Assert.False(File.Exists(moved));
    }
}
