using System.IO;
using System.Windows.Media.Imaging;

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
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is BitmapMetadata meta && meta.DateTaken is string dateStr
                && DateTime.TryParse(dateStr, out var dt))
                return dt;
        }
        catch { }
        return null;
    }
}
