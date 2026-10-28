using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Icons;

/// <summary>
/// Gets the icon Explorer shows for a file, shortcut or <c>shell:AppsFolder\AUMID</c> item, with alpha.
/// One shell API covers every target kind, including packaged apps.
/// </summary>
public static class IconExtractor
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    private static readonly int[] CandidateSizes = [256, 128, 64, 48, 32];

    /// <summary>Visible content must span this share of the image for a size to count as a native icon size.</summary>
    private const double NativeFill = 0.6;

    /// <summary>
    /// Largest size at which the icon really fills the image. Apps that ship only small icons come back from the
    /// shell at 256 px as a small glyph inside a faint frame; their native size (48, 32…) looks far better scaled
    /// up. Falls back to the size with the most visible content.
    /// </summary>
    public static Bitmap? ExtractBest(string source)
    {
        if (source.StartsWith(TargetResolver.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            && PackageLogo.Load(source[TargetResolver.AppsFolderPrefix.Length..]) is { } logo)
            return logo;

        Bitmap? best = null;
        var bestContent = 0;
        foreach (var size in CandidateSizes)
        {
            var icon = Extract(source, size);
            if (icon is null)
                continue;
            var bounds = KeyRenderer.ContentBounds(icon);
            var content = Math.Max(bounds.Width, bounds.Height);
            if (content >= icon.Width * NativeFill)
            {
                best?.Dispose();
                return icon;
            }
            if (content > bestContent)
            {
                best?.Dispose();
                best = icon;
                bestContent = content;
            }
            else
            {
                icon.Dispose();
            }
        }
        return best;
    }

    public static Bitmap? Extract(string source, int size = 256)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(ShellParsingName(source), IntPtr.Zero, ref iid, out var obj) != 0 || obj is not IShellItemImageFactory factory)
            return null;

        try
        {
            if (factory.GetImage(new NativeSize(size, size), SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out var hbitmap) != 0)
                return null;
            try
            {
                return FromHBitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }
    }

    /// <summary>The shell rejects forward slashes that the file system accepts; rooted paths are normalized.</summary>
    private static string ShellParsingName(string source) =>
        Path.IsPathRooted(source) ? Path.GetFullPath(source) : source;

    /// <summary>
    /// A custom icon: <c>file,index</c> names the index-th icon of an exe, dll or .ico (a negative index is a resource
    /// id); a .ico without index means its first icon; an image file is drawn as is; anything else gets its shell icon.
    /// </summary>
    public static Bitmap? LoadCustomIcon(string icon)
    {
        var (file, index) = ParseIconRef(icon);
        if (index is not null || Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return ExtractIndexed(file, index ?? 0);
        return IconCatalog.IsImageFile(file) ? LoadImageFile(file) : ExtractBest(file);
    }

    public static (string File, int? Index) ParseIconRef(string icon)
    {
        var comma = icon.LastIndexOf(',');
        return comma > 0 && int.TryParse(icon.AsSpan(comma + 1).Trim(), out var index)
            ? (icon[..comma].Trim(), index)
            : (icon.Trim(), null);
    }

    /// <summary>Number of icons in an exe, dll or .ico; 0 for anything else.</summary>
    public static int CountIcons(string file) => File.Exists(file) ? (int)ExtractIconExW(file, -1, null, null, 0) : 0;

    /// <summary>One icon of an exe, dll or .ico at the requested size (the closest the file holds, scaled), with alpha.</summary>
    public static Bitmap? ExtractIndexed(string file, int index, int size = 256)
    {
        if (!File.Exists(file))
            return null;
        var handles = new IntPtr[1];
        var ids = new int[1];
        if (PrivateExtractIconsW(file, index, size, size, handles, ids, 1, 0) != 1 || handles[0] == IntPtr.Zero)
            return null;
        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    public static Bitmap? LoadImageFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the bitmap's pixels top-down through GetDIBits. <see cref="Image.FromHbitmap(IntPtr)"/> would drop
    /// the alpha channel. Shell bitmaps carry premultiplied alpha; one with no alpha at all is treated as opaque.
    /// </summary>
    private static Bitmap? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
            return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var pixels = new byte[width * height * 4];

        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
            hasAlpha = pixels[i] != 0;

        var bitmap = new Bitmap(width, height, hasAlpha ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

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
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
