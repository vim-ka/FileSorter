using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class NamePrefixTests
{
    [Theory]
    [InlineData("hello-world-1.jpg", "hello-world")]
    [InlineData("IMG_2034.jpg", "IMG")]
    [InlineData("photo (3).png", "photo")]
    [InlineData("IMG2034.jpg", "IMG")]
    [InlineData("file2.txt", "file")]
    [InlineData("report_v2.docx", "report_v")]
    [InlineData("2034.jpg", "#")]
    [InlineData("README", "README")]
    public void Get_StripsTrailingNumbersAndSeparators(string name, string expected) =>
        Assert.Equal(expected, NamePrefix.Get(name));
}
