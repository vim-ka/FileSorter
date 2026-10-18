using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileSorter.Platform;

/// <summary>
/// Extracts file thumbnails using the Windows Shell IShellItemImageFactory API.
/// Returns the same thumbnails that Windows Explorer displays for PDF, DOCX, XLSX, etc.
/// </summary>
public static class ThumbnailService
{
    #region COM Interop

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
        Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] NativeSize size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;

        public NativeSize(int width, int height)
        {
            cx = width;
            cy = height;
        }
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
        WideThumbnails = 0x40,
        IconBackground = 0x80,
        ScaleUp = 0x100,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItem ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    #endregion

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>
    /// Gets a thumbnail for the specified file using the Windows Shell thumbnail provider.
    /// Returns the same thumbnail that Windows Explorer would show.
    /// Works for PDF, DOCX, XLSX, PPTX, images, videos, and any file type with a registered thumbnail handler.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="size">Desired thumbnail size in pixels (width and height). Default is 256.</param>
    /// <returns>A frozen BitmapSource, or null if no thumbnail could be generated.</returns>
    public static BitmapSource? GetThumbnail(string filePath, int size = 256)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, IID_IShellItem, out IShellItem shellItem);
            if (hr != 0)
                return null;

            try
            {
                var imageFactory = (IShellItemImageFactory)shellItem;
                hr = imageFactory.GetImage(new NativeSize(size, size), SIIGBF.BiggerSizeOk, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                return ToBitmapSource(hBitmap);
            }
            finally
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            // IShellItem doesn't support IShellItemImageFactory for this item
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
        }
    }

    /// <summary>
    /// Copies the HBITMAP pixels as top-down 32bpp BGRA. Imaging.CreateBitmapSourceFromHBitmap
    /// drops the alpha channel, which turns transparent shell icons black.
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
            return null;

        int width = bm.bmWidth;
        int height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0)
            return null;

        int stride = width * 4;
        var pixels = new byte[stride * height];
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0 // BI_RGB
        };

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // Bitmaps without an alpha channel come back with alpha = 0 everywhere
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }

        // Shell thumbnails with alpha are premultiplied
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        source.Freeze(); // Make it usable across threads
        return source;
    }
}
