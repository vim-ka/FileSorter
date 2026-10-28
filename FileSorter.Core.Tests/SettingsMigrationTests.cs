using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class SettingsMigrationTests : TempDataDirTest
{
    private void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(TempDir, "settings.json"), json);

    private const string LegacyJson = """
        {
          "RecentFolders": [
            { "FullPath": "C:/Users/a/Downloads", "Name": "Downloads", "FileCount": 5 },
            { "FullPath": "C:/Users/a/Desktop", "Name": "Desktop", "FileCount": 7 }
          ],
          "CopyMode": true,
          "SelectedMethod": "ByDate",
          "DestinationSubfolderPattern": "#year",
          "SkipDuplicates": true,
          "IsDarkTheme": false
        }
        """;

    [Fact]
    public void Load_LegacyFile_CopiesGlobalSettingsIntoEveryRecentFolder()
    {
        WriteSettings(LegacyJson);

        var s = SettingsService.Load();

        Assert.Equal(2, s.Folders.Count);
        Assert.All(s.Folders, f =>
        {
            Assert.True(f.CopyMode);
            Assert.Equal(OrganizeMethod.ByDate, f.Method);
            Assert.Equal("#year", f.Template);
            Assert.True(f.SkipDuplicates);
        });
        Assert.Equal("Downloads", s.Folders[0].Name);
        Assert.Equal(5, s.Folders[0].FileCount);
        Assert.False(s.IsDarkTheme);
        Assert.Equal(2, s.Version);
    }

    [Fact]
    public void Load_LegacyDuplicatePaths_Collapse()
    {
        WriteSettings("""
            { "RecentFolders": [
              { "FullPath": "C:/x/Dl" }, { "FullPath": "c:/x/dl/" } ] }
            """);

        Assert.Single(SettingsService.Load().Folders);
    }

    [Fact]
    public void Load_LegacyNumericEnums_Accepted()
    {
        WriteSettings("""
            { "RecentFolders": [ { "FullPath": "C:/x" } ], "SelectedMethod": 2 }
            """);

        Assert.Equal(OrganizeMethod.ByDate, SettingsService.Load().Folders[0].Method);
    }

    [Fact]
    public void Load_V2_RoundTrips()
    {
        var profile = FolderProfile.CreateDefault(@"C:\Users\a\Downloads");
        profile.Auto.Enabled = true;
        profile.Rules.Add(new RoutingRule
        {
            Destination = @"D:\Invoices",
            MatchAll = false,
            Conditions = { new FilterRule { Field = RuleField.Name, Operator = RuleOperator.StartsWith, Value = "inv" } }
        });
        SettingsService.Save(new AppSettings { Folders = { profile }, PauseAll = true });

        var loaded = SettingsService.Load();

        Assert.Equal(2, loaded.Version);
        Assert.True(loaded.PauseAll);
        var p = Assert.Single(loaded.Folders);
        Assert.True(p.Auto.Enabled);
        var rule = Assert.Single(p.Rules);
        Assert.Equal(@"D:\Invoices", rule.Destination);
        Assert.False(rule.MatchAll);
        Assert.Equal("inv", Assert.Single(rule.Conditions).Value);
    }

    [Fact]
    public void Load_InvalidEnumInProfile_FallsBackToDefault()
    {
        WriteSettings("""
            { "Version": 2, "Folders": [ { "FullPath": "C:/x", "Method": 99 } ] }
            """);

        Assert.Equal(OrganizeMethod.ByType, SettingsService.Load().Folders[0].Method);
    }

    [Fact]
    public void Load_V2_DropsEmptyPathsAndCollapsesDuplicates()
    {
        WriteSettings("""
            { "Version": 2, "Folders": [
              { "FullPath": "" },
              { "FullPath": "   " },
              { "FullPath": "C:/x/Dl", "CopyMode": true },
              { "FullPath": "c:/x/dl/" },
              { "FullPath": "C:/x/Other" } ] }
            """);

        var folders = SettingsService.Load().Folders;

        Assert.Equal(["C:/x/Dl", "C:/x/Other"], folders.Select(f => f.FullPath));
        Assert.True(folders[0].CopyMode); // the first occurrence wins
    }

    [Fact]
    public void FindProfile_IgnoresCaseAndTrailingSeparator()
    {
        var s = new AppSettings { Folders = { FolderProfile.CreateDefault(@"C:\Users\a\Downloads") } };

        Assert.NotNull(s.FindProfile(@"c:\users\a\downloads\"));
        Assert.Null(s.FindProfile(@"c:\users\a\other"));
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var s = SettingsService.Load();

        Assert.Equal(2, s.Version);
        Assert.Empty(s.Folders);
        Assert.True(s.IsDarkTheme);
    }

    [Fact]
    public void CreateDefault_NameIsLastSegmentOrDriveRoot()
    {
        Assert.Equal("Downloads", FolderProfile.CreateDefault(@"C:\Users\a\Downloads\").Name);
        Assert.Equal(@"C:\", FolderProfile.CreateDefault(@"C:\").Name);
    }

    [Fact]
    public async Task SaveInBackground_FileBrieflyOpenElsewhere_RetriedAndLatestLands()
    {
        SettingsService.Save(new AppSettings { PauseAll = false });
        var path = Path.Combine(TempDir, "settings.json");

        Task save;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // blocks the replace
        {
            save = SettingsService.SaveInBackground(new AppSettings { PauseAll = true });
            await Task.Delay(120);
        }
        await save;

        Assert.True(SettingsService.Load().PauseAll);
    }

    [Fact]
    public async Task SaveInBackground_OlderSaveFinishingLate_DoesNotOverwriteNewer()
    {
        var older = SettingsService.SaveInBackground(new AppSettings { StartWithWindows = false });
        SettingsService.Save(new AppSettings { StartWithWindows = true });
        await older;

        Assert.True(SettingsService.Load().StartWithWindows);
    }
}
