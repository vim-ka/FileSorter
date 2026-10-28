using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class PlannerTests : TempDataDirTest
{
    private static readonly List<FileCategory> Categories = FileCategoryService.GetDefaultCategories();

    private string Target => Path.Combine(TempDir, "src");

    private OrganizeRequest Request(UnmatchedFilesMode unmatched = UnmatchedFilesMode.OrganizeNormally,
        params RoutingRule[] rules)
    {
        var profile = FolderProfile.CreateDefault(Target);
        profile.Unmatched = unmatched;
        profile.Rules.AddRange(rules);
        return OrganizeRequest.FromProfile(profile, Categories);
    }

    internal static RoutingRule Rule(string destination, RuleField field, RuleOperator op, string value) => new()
    {
        Destination = destination,
        Conditions = [new FilterRule { Field = field, Operator = op, Value = value }]
    };

    private FileItem File(string name) => TestData.Item(Path.Combine(Target, name));

    [Fact]
    public void Plan_FirstMatchingRuleWins()
    {
        var request = Request(rules:
        [
            Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4"),
            Rule("Trips", RuleField.Name, RuleOperator.Contains, "trip")
        ]);

        var plan = FileOrganizerService.Plan([File("trip.mp4")], request);

        var group = Assert.Single(plan);
        Assert.Equal(Path.Combine(Target, "Movies"), group.FolderPath);
        Assert.Equal(["Movies"], group.DisplaySegments);
    }

    [Fact]
    public void Plan_RuleWithFullPath_UsesItAsIs()
    {
        var video = Path.Combine(TempDir, "Video");
        var request = Request(rules: [Rule(video, RuleField.Extension, RuleOperator.Is, "mp4")]);

        var plan = FileOrganizerService.Plan([File("clip.mp4")], request);

        var group = Assert.Single(plan);
        Assert.Equal(video, group.FolderPath);
        Assert.Single(group.DisplaySegments);
        Assert.Equal(video, group.DisplaySegments[0]);
    }

    [Fact]
    public void Plan_Unmatched_LeaveInPlace_ExcludesFile()
    {
        var request = Request(UnmatchedFilesMode.LeaveInPlace,
            Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4"));

        var plan = FileOrganizerService.Plan([File("clip.mp4"), File("notes.txt")], request);

        var group = Assert.Single(plan);
        Assert.Equal("clip.mp4", Assert.Single(group.Files).FileName);
    }

    [Fact]
    public void Plan_Unmatched_OrganizeNormally_UsesMethod()
    {
        var request = Request(UnmatchedFilesMode.OrganizeNormally,
            Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4"));

        var plan = FileOrganizerService.Plan([File("clip.mp4"), File("notes.txt")], request);

        Assert.Equal(2, plan.Count);
        var docs = Assert.Single(plan, g => g.FolderPath == Path.Combine(Target, "Documents"));
        Assert.Equal("notes.txt", Assert.Single(docs.Files).FileName);
        Assert.Equal(["Documents"], docs.DisplaySegments);
    }

    [Fact]
    public void Plan_DisabledRuleOrEmptyDestination_Ignored()
    {
        var disabled = Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4");
        disabled.IsEnabled = false;
        var empty = Rule("  ", RuleField.Extension, RuleOperator.Is, "mp4");
        Assert.Null(disabled.Snapshot());
        Assert.Null(empty.Snapshot());

        var request = Request(rules: [disabled, empty]);
        Assert.Empty(request.Rules);

        var plan = FileOrganizerService.Plan([File("clip.mp4")], request);

        Assert.Equal(Path.Combine(Target, "Videos"), Assert.Single(plan).FolderPath);
    }

    [Fact]
    public void Plan_RuleWithoutConditions_MatchesAnyFile()
    {
        var request = Request(rules: [new RoutingRule { Destination = "All" }]);

        var plan = FileOrganizerService.Plan([File("a.txt"), File("b.mp4")], request);

        Assert.Equal(2, Assert.Single(plan).Files.Count);
    }

    [Fact]
    public void Plan_MatchAny_MatchesWhenOneConditionHolds()
    {
        var rule = Rule("Media", RuleField.Extension, RuleOperator.Is, "mp4");
        rule.Conditions.Add(new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "mp3" });
        rule.MatchAll = false;
        var all = Rule("Media", RuleField.Extension, RuleOperator.Is, "mp4");
        all.Conditions.Add(new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is, Value = "mp3" });

        Assert.True(rule.Snapshot()!.Matches(File("a.mp3")));
        Assert.False(all.Snapshot()!.Matches(File("a.mp3")));
    }

    [Fact]
    public void Plan_ApplyFiltersOff_IncludesAllCategories()
    {
        var profile = FolderProfile.CreateDefault(Target);
        profile.DisabledCategories.Add("Images");
        profile.ApplyFilters = false;
        var request = OrganizeRequest.FromProfile(profile, Categories);
        Assert.Null(request.AllowedCategories);

        var plan = FileOrganizerService.Plan([File("photo.png")], request);

        Assert.Equal(Path.Combine(Target, "Images"), Assert.Single(plan).FolderPath);
    }

    [Fact]
    public void Plan_CategoryFilterAppliedBeforeRules()
    {
        var profile = FolderProfile.CreateDefault(Target);
        profile.DisabledCategories.Add("Images");
        profile.Rules.Add(Rule("Pics", RuleField.Name, RuleOperator.Contains, "photo"));
        var request = OrganizeRequest.FromProfile(profile, Categories);

        var plan = FileOrganizerService.Plan([File("photo.png"), File("photo.txt")], request);

        var group = Assert.Single(plan);
        Assert.Equal(Path.Combine(Target, "Pics"), group.FolderPath);
        Assert.Equal("photo.txt", Assert.Single(group.Files).FileName);
    }

    [Fact]
    public void Plan_GroupsByFolderPathIgnoringCase()
    {
        var request = Request(rules:
        [
            Rule("Movies", RuleField.Extension, RuleOperator.Is, "mp4"),
            Rule("movies", RuleField.Extension, RuleOperator.Is, "mkv")
        ]);

        var plan = FileOrganizerService.Plan([File("a.mp4"), File("b.mkv")], request);

        Assert.Equal(2, Assert.Single(plan).Files.Count);
    }

    [Fact]
    public void FromProfile_CopiesProfileValues()
    {
        var profile = FolderProfile.CreateDefault(Target);
        profile.DestinationFolder = Path.Combine(TempDir, "dest");
        profile.Method = OrganizeMethod.ByDate;
        profile.DateGranularity = DateGranularity.Year;
        profile.Template = "#ext";
        profile.CopyMode = true;
        profile.SkipDuplicates = true;
        profile.DisabledCategories.Add("Images");

        var request = OrganizeRequest.FromProfile(profile, Categories);
        profile.DisabledCategories.Add("Videos"); // later edits must not leak into the request

        Assert.Equal(Target, request.SourceFolder);
        Assert.Equal(profile.DestinationFolder, request.TargetFolder);
        Assert.Equal(OrganizeMethod.ByDate, request.Method);
        Assert.Equal("yyyy", request.DateFormat);
        Assert.Equal("#ext", request.Template);
        Assert.True(request.CopyMode);
        Assert.True(request.SkipDuplicates);
        Assert.DoesNotContain("Images", request.AllowedCategories!);
        Assert.Contains("Videos", request.AllowedCategories!);
    }

    [Fact]
    public void FromProfile_BlankTemplateAndNoDestination()
    {
        var profile = FolderProfile.CreateDefault(Target);
        profile.Template = "   ";

        var request = OrganizeRequest.FromProfile(profile, Categories);

        Assert.Null(request.Template);
        Assert.Equal(Target, request.TargetFolder);
    }
}
