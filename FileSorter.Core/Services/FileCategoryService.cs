using FileSorter.Models;

namespace FileSorter.Services;

public static class FileCategoryService
{
    public static List<FileCategory> GetDefaultCategories() =>
    [
        new FileCategory
        {
            Name = "Documents",
            Icon = "\uE8A5",
            Extensions = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".odp", ".csv", ".epub", ".pages", ".numbers"]
        },
        new FileCategory
        {
            Name = "Images",
            Icon = "\uE8B9",
            Extensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff", ".tif", ".raw", ".cr2", ".nef", ".psd", ".ai", ".heic", ".heif"]
        },
        new FileCategory
        {
            Name = "Videos",
            Icon = "\uE8B2",
            Extensions = [".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts"]
        },
        new FileCategory
        {
            Name = "Music",
            Icon = "\uE8D6",
            Extensions = [".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff", ".alac"]
        },
        new FileCategory
        {
            Name = "Archives",
            Icon = "\uE8C8",
            Extensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab"]
        },
        new FileCategory
        {
            Name = "Code",
            Icon = "\uE943",
            Extensions = [".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".html", ".css", ".json", ".xml", ".yaml", ".yml", ".sql", ".php", ".rb", ".go", ".rs", ".swift", ".kt"]
        },
        new FileCategory
        {
            Name = "Executables",
            Icon = "\uE756",
            Extensions = [".exe", ".msi", ".bat", ".cmd", ".ps1", ".sh", ".app", ".dmg", ".deb", ".rpm"]
        },
        new FileCategory
        {
            Name = "Fonts",
            Icon = "\uE8D2",
            Extensions = [".ttf", ".otf", ".woff", ".woff2", ".eot"]
        },
        new FileCategory
        {
            Name = "Other",
            Icon = "\uE7C3",
            Extensions = []
        }
    ];

    public static string GetCategoryName(string extension, IReadOnlyList<FileCategory> categories)
    {
        foreach (var category in categories)
        {
            if (category.Name == "Other") continue;
            if (category.Extensions.Contains(extension.ToLowerInvariant()))
                return category.Name;
        }
        return "Other";
    }
}
