using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class ProfileConflictsTests
{
    private static FolderProfile Profile(string path, bool auto = false, string? destination = null)
    {
        var p = FolderProfile.CreateDefault(path);
        p.Auto.Enabled = auto;
        p.DestinationFolder = destination;
        return p;
    }

    private static RoutingRule Rule(string destination) => new() { Destination = destination };

    [Fact]
    public void NoOtherAutoFolders_NoWarnings()
    {
        var a = Profile(@"C:\A", auto: true, destination: @"C:\B");
        var b = Profile(@"C:\B"); // not auto

        Assert.Empty(ProfileConflicts.Find(a, [a, b]));
    }

    [Theory]
    [InlineData(@"C:\Downloads")]
    [InlineData(@"C:\Downloads\")]
    [InlineData(@"c:\downloads\Sorted")]
    public void DestinationEqualOrInsideOtherAutoFolder_Warns(string destination)
    {
        var a = Profile(@"C:\A", destination: destination);
        var downloads = Profile(@"C:\Downloads", auto: true);

        var warning = Assert.Single(ProfileConflicts.Find(a, [a, downloads]));
        Assert.Contains("Downloads", warning);
        Assert.Contains("Destination", warning);
    }

    [Fact]
    public void DestinationBesideOtherAutoFolder_NoWarning()
    {
        var a = Profile(@"C:\A", destination: @"C:\DownloadsArchive");
        var downloads = Profile(@"C:\Downloads", auto: true);

        Assert.Empty(ProfileConflicts.Find(a, [a, downloads]));
    }

    [Fact]
    public void FullPathRuleInsideOtherAutoFolder_Warns()
    {
        var a = Profile(@"C:\A", auto: true);
        a.Rules.Add(Rule(@"D:\Movies"));
        a.Rules.Add(Rule(@"C:\Downloads\Inbox"));
        a.Rules.Add(Rule("Relative")); // stays inside A's own target
        var downloads = Profile(@"C:\Downloads", auto: true);

        var warning = Assert.Single(ProfileConflicts.Find(a, [a, downloads]));
        Assert.Contains(@"C:\Downloads\Inbox", warning);
        Assert.Contains("Downloads", warning);
    }

    [Fact]
    public void DisabledRule_Ignored()
    {
        var a = Profile(@"C:\A");
        var rule = Rule(@"C:\Downloads");
        rule.IsEnabled = false;
        a.Rules.Add(rule);
        var downloads = Profile(@"C:\Downloads", auto: true);

        Assert.Empty(ProfileConflicts.Find(a, [a, downloads]));
    }

    [Theory]
    [InlineData(@"C:\Data\Downloads", @"C:\Data")]
    [InlineData(@"C:\Data", @"C:\Data\Downloads")]
    public void NestedAutoRoots_Warns(string root, string other)
    {
        var a = Profile(root, auto: true);
        var b = Profile(other, auto: true);

        var warning = Assert.Single(ProfileConflicts.Find(a, [a, b]));
        Assert.Contains(b.Name, warning);
    }

    [Fact]
    public void NestedRoots_OnlyOneAuto_NoWarning()
    {
        var a = Profile(@"C:\Data\Downloads");
        var b = Profile(@"C:\Data", auto: true);

        Assert.Empty(ProfileConflicts.Find(a, [a, b]));
    }

    [Fact]
    public void ProfileItself_NotComparedWithItself()
    {
        var a = Profile(@"C:\A", auto: true, destination: @"C:\A\Sorted");

        Assert.Empty(ProfileConflicts.Find(a, [a, Profile(@"c:\a\", auto: true)]));
    }

    [Fact]
    public void FindIncoming_OtherAutoFolderSendsIntoTarget_Warns()
    {
        var downloads = Profile(@"C:\Downloads", auto: true);
        var desktop = Profile(@"C:\Desktop", auto: true, destination: @"C:\Downloads\Sorted");
        var rule = Rule(@"C:\Downloads");
        rule.Conditions = [new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "mp4" }];
        var music = Profile(@"C:\Music", auto: true);
        music.Rules = [rule];
        var manual = Profile(@"C:\Manual", destination: @"C:\Downloads"); // not auto: not reported here

        var warnings = ProfileConflicts.FindIncoming(downloads, [downloads, desktop, music, manual]);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("Desktop"));
        Assert.Contains(warnings, w => w.Contains("Music"));
    }

    [Fact]
    public void FindIncoming_TargetNotAuto_NoWarnings()
    {
        var downloads = Profile(@"C:\Downloads");
        var desktop = Profile(@"C:\Desktop", auto: true, destination: @"C:\Downloads");

        Assert.Empty(ProfileConflicts.FindIncoming(downloads, [downloads, desktop]));
    }
}
