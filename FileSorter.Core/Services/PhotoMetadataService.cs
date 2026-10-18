using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace FileSorter.Services;

public static class PhotoMetadataService
{
    private static readonly HashSet<string> ExifExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".tiff", ".tif", ".heic", ".heif", ".cr2", ".nef"
    };

    public static bool IsExifSupported(string path) =>
        ExifExtensions.Contains(Path.GetExtension(path));

    public static DateTime? GetDateTaken(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                return dt;

            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out dt))
                return dt;
        }
        catch { }
        return null;
    }
}
