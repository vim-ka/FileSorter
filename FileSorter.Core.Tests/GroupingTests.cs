using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class GroupingTests
{
    private readonly FileOrganizerService _svc = new();
    private readonly List<FileCategory> _categories = FileCategoryService.GetDefaultCategories();

    private static string RootKey(IEnumerable<PreviewItem> preview) =>
        Assert.Single(preview).Name;

    [Fact]
    public void DateTakenToken_FallsBackToModifiedMonth_WhenNoExif()
    {
        var file = TestData.Item(@"C:\x\notes.txt", new DateTime(2024, 5, 1));

        var preview = _svc.GeneratePreview([file], OrganizeMethod.ByType, _categories, template: "#datetaken");

        Assert.Equal("2024-05", RootKey(preview));
    }

    [Fact]
    public void DateToken_StillExpandsNextToDateTaken()
    {
        var file = TestData.Item(@"C:\x\notes.txt", new DateTime(2024, 5, 1));

        var preview = _svc.GeneratePreview([file], OrganizeMethod.ByType, _categories, template: "#date/#datetaken");

        var root = Assert.Single(preview);
        Assert.Equal("2024-05-01", root.Name);
        Assert.Equal("2024-05", Assert.Single(root.Children).Name);
    }

    [Fact]
    public void GeneratePreview_DoesNotDropFilesOfDeselectedCategories()
    {
        // Category filtering is the ViewModel's job (it honours ApplyFilters);
        // the service must group whatever it is given.
        var images = _categories.First(c => c.Name == "Images");
        images.IsSelected = false;
        var file = TestData.Item(@"C:\x\photo.png");

        var preview = _svc.GeneratePreview([file], OrganizeMethod.ByType, _categories);

        Assert.Equal("Images", RootKey(preview));
    }
}
