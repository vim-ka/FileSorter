using FileSorter.Models;
using FileSorter.ViewModels;

namespace FileSorter.Core.Tests;

public class RulesEditorTests : TempDataDirTest
{
    private readonly VmHost _host = new();

    protected override void BeforeDispose() => _host.Dispose();

    private FakeDialogs _dialogs => _host.Dialogs;

    internal MainViewModel NewVm() =>
        _host.NewVm();

    internal FolderViewModel AddFolder(MainViewModel vm, string path)
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

    /// <summary>
    /// Switches to grouping by extension and waits for that preview: refreshes run in order and
    /// the last one reads the current profile, so the result reflects every edit made before.
    /// </summary>
    private static async Task<List<string>> SettledPreviewByExtension(MainViewModel vm)
    {
        vm.SelectedMethod = OrganizeMethod.ByExtension;
        await Wait.UntilAsync(() => !vm.IsScanning && PreviewFolders(vm).Contains("TXT"));
        return PreviewFolders(vm);
    }

    private static async Task<MainViewModel> ScannedVm(RulesEditorTests t, string dir)
    {
        var vm = t.NewVm();
        t.AddFolder(vm, dir);
        await Wait.UntilAsync(() => vm.StatusText.Contains("will be organized"));
        return vm;
    }

    [Fact]
    public Task AddRule_AppearsInProfile() => _host.Run(() =>
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.RulesActiveText)) raised++; };
        Assert.Equal("", vm.RulesActiveText);

        vm.AddRuleCommand.Execute(null);

        var rule = Assert.Single(vm.Rules);
        Assert.Same(a.Profile.Rules.Single(), rule.Model);
        Assert.True(rule.IsEnabled);
        Assert.Equal("", vm.RulesActiveText); // no destination, blank condition: not effective yet

        rule.Destination = "Movies";
        rule.MatchAll = false;
        Assert.Equal("Movies", a.Profile.Rules[0].Destination);
        Assert.False(a.Profile.Rules[0].MatchAll);
        Assert.Equal("", vm.RulesActiveText); // the seeded condition is still blank

        raised = 0;
        rule.Conditions[0].Value = "mp4";
        Assert.Equal("RULES ON", vm.RulesActiveText);
        Assert.True(raised > 0);

        raised = 0;
        rule.IsEnabled = false;
        Assert.False(a.Profile.Rules[0].IsEnabled);
        Assert.Equal("", vm.RulesActiveText);
        Assert.True(raised > 0);
    });

    [Fact]
    public Task NewRule_BlankSeededCondition_LeavesPreviewUnchangedUntilFilled() => _host.Run(async () =>
    {
        var dir = TestData.NewTempDir("Src");
        TestData.WriteFile(dir, "clip.mp4");
        TestData.WriteFile(dir, "notes.txt");
        var vm = await ScannedVm(this, dir);

        vm.AddRuleCommand.Execute(null);
        var rule = vm.Rules.Single();
        rule.Destination = "Movies";

        Assert.Equal(["MP4", "TXT"], (await SettledPreviewByExtension(vm)).Order());

        rule.Conditions.Single().Value = "mp4";

        await Wait.UntilAsync(() => PreviewFolders(vm).Contains("Movies"));
        Assert.Equal(["Movies", "TXT"], PreviewFolders(vm).Order());
    });

    [Fact]
    public Task RuleWithoutFolder_SelectsFilesThatAreOrganizedByTheMethod() => _host.Run(async () =>
    {
        var dir = TestData.NewTempDir("Src");
        TestData.WriteFile(dir, "clip.mp4");
        TestData.WriteFile(dir, "notes.txt");
        var vm = await ScannedVm(this, dir);
        vm.Unmatched = UnmatchedFilesMode.LeaveInPlace;

        vm.AddRuleCommand.Execute(null);
        Assert.Equal("", vm.RulesActiveText); // blank condition value: not active yet
        vm.Rules.Single().Conditions.Single().Value = "mp4";

        Assert.Equal("RULES ON", vm.RulesActiveText);
        vm.SelectedMethod = OrganizeMethod.ByExtension;
        await Wait.UntilAsync(() => !vm.IsScanning && PreviewFolders(vm).SequenceEqual(["MP4"]));
    });

    [Fact]
    public Task RulesActiveText_UnparsableSizeValue_StaysOff() => _host.Run(() =>
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        vm.AddRuleCommand.Execute(null);
        var rule = vm.Rules.Single();
        rule.Destination = "Big";
        var condition = rule.Conditions.Single();
        condition.Field = RuleField.Size;
        condition.Operator = RuleOperator.GreaterThan;

        condition.Value = "abc";
        Assert.Equal("", vm.RulesActiveText);

        condition.Value = "1,5";
        Assert.Equal("RULES ON", vm.RulesActiveText);
    });

    [Fact]
    public Task Conditions_WriteIntoTheRuleModel() => _host.Run(() =>
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
    });

    [Fact]
    public Task RuleFolder_HiddenUntilAdded_RemovingItClearsTheDestination() => _host.Run(() =>
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        vm.AddRuleCommand.Execute(null);
        var rule = vm.Rules.Single();
        Assert.False(rule.HasFolder); // new rule: files go to the destination folder itself

        rule.AddFolderCommand.Execute(null);
        Assert.True(rule.HasFolder);
        Assert.Equal("", rule.Destination); // shown, still empty

        rule.Destination = "Movies";
        rule.RemoveFolderCommand.Execute(null);
        Assert.False(rule.HasFolder);
        Assert.Equal("", rule.Model.Destination);

        // a rule saved with a folder shows it
        Assert.True(new RoutingRuleViewModel(new RoutingRule { Destination = "Trips" }).HasFolder);
    });

    [Fact]
    public Task LastCondition_CannotBeRemoved_SoARuleNeverSilentlyMatchesEverything() => _host.Run(() =>
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        var rule = AddRule(vm, "A");
        var only = rule.Conditions.Single();
        Assert.False(only.RemoveCommand.CanExecute(null));
        only.RemoveCommand.Execute(null);
        Assert.Single(rule.Conditions);
        Assert.Single(rule.Model.Conditions);

        int raised = 0;
        only.RemoveCommand.CanExecuteChanged += (_, _) => raised++;
        rule.AddConditionCommand.Execute(null);
        Assert.True(raised > 0);
        Assert.True(only.RemoveCommand.CanExecute(null));
        Assert.True(rule.Conditions[1].RemoveCommand.CanExecute(null));

        rule.Conditions[1].RemoveCommand.Execute(null);
        Assert.Same(only, rule.Conditions.Single());
        Assert.False(only.RemoveCommand.CanExecute(null));
    });

    [Fact]
    public Task MoveRuleCommands_DisabledWhereThereIsNowhereToMove() => _host.Run(() =>
    {
        var vm = NewVm();
        AddFolder(vm, TestData.NewTempDir("A"));
        var a = AddRule(vm, "A");
        Assert.False(vm.MoveRuleUpCommand.CanExecute(a));
        Assert.False(vm.MoveRuleDownCommand.CanExecute(a));

        int raised = 0;
        vm.MoveRuleDownCommand.CanExecuteChanged += (_, _) => raised++;
        var b = AddRule(vm, "B");
        Assert.True(raised > 0);
        Assert.False(vm.MoveRuleUpCommand.CanExecute(a));
        Assert.True(vm.MoveRuleDownCommand.CanExecute(a));
        Assert.True(vm.MoveRuleUpCommand.CanExecute(b));
        Assert.False(vm.MoveRuleDownCommand.CanExecute(b));

        raised = 0;
        vm.MoveRuleUpCommand.Execute(b);
        Assert.True(raised > 0);
        Assert.False(vm.MoveRuleUpCommand.CanExecute(b));
        Assert.True(vm.MoveRuleDownCommand.CanExecute(b));
    });

    [Fact]
    public Task MoveRuleUp_ReordersProfileRules() => _host.Run(() =>
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
    });

    [Fact]
    public Task RemoveRule_RemovesFromProfile() => _host.Run(() =>
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var first = AddRule(vm, "A");
        AddRule(vm, "B");

        vm.RemoveRuleCommand.Execute(first);

        Assert.Equal(["B"], a.Profile.Rules.Select(r => r.Destination));
        Assert.Equal(["B"], vm.Rules.Select(r => r.Destination));
    });

    [Fact]
    public Task BrowseRuleDestination_SetsPickedFolder() => _host.Run(() =>
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
    });

    [Fact]
    public Task Unmatched_WritesIntoProfile() => _host.Run(() =>
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
    });

    [Fact]
    public Task EditingCondition_RefreshesPreview() => _host.Run(async () =>
    {
        var dir = TestData.NewTempDir("Src");
        TestData.WriteFile(dir, "clip.mp4");
        TestData.WriteFile(dir, "notes.txt");
        var vm = NewVm();
        AddFolder(vm, dir);
        await Wait.UntilAsync(() => vm.StatusText.Contains("will be organized"));

        var rule = AddRule(vm, "Movies");
        var condition = rule.Conditions.Single();
        condition.Field = RuleField.Extension;
        condition.Operator = RuleOperator.Is;
        condition.Value = "zip";
        await Wait.UntilAsync(() => !vm.IsScanning && PreviewFolders(vm).Count > 0
                                     && !PreviewFolders(vm).Contains("Movies"));

        condition.Value = "mp4";

        await Wait.UntilAsync(() => PreviewFolders(vm).Contains("Movies"));
    });

    [Fact]
    public Task SwitchFolder_RebuildsRulesFromItsProfile() => _host.Run(() =>
    {
        var vm = NewVm();
        var a = AddFolder(vm, TestData.NewTempDir("A"));
        var ruleA = AddRule(vm, "Movies");
        ruleA.Conditions[0].Value = "mp4";
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

        // a view model of the previous rebuild must not notify or refresh the main view model
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        ruleA.Destination = "Stale";
        ruleA.Conditions[0].Value = "mkv";
        ruleA.IsEnabled = false;
        Assert.Empty(raised);

        rebuilt.Destination = "Films";
        Assert.Equal(["Docs", "Music"], b.Profile.Rules.Select(r => r.Destination));

        vm.SelectFolderCommand.Execute(b);
        Assert.Equal(["Docs", "Music"], vm.Rules.Select(r => r.Destination));
    });

    [Fact]
    public Task RuleToFullPath_ShowsInPreview_OrganizesAndUndoes() => _host.Run(async () =>
    {
        var src = TestData.NewTempDir("Src");
        var video = TestData.NewTempDir("Video");
        var clip = TestData.WriteFile(src, "clip.mp4");
        TestData.WriteFile(src, "notes.txt");
        var vm = NewVm();
        AddFolder(vm, src);
        await Wait.UntilAsync(() => vm.StatusText.Contains("will be organized"));

        var rule = AddRule(vm, video);
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
    });
}
