using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace ManagedLauncher.Core.Icons;

/// <summary>
/// A packaged (Store) app's icon from its own tile assets. The shell only offers the bare taskbar glyph, often
/// upscaled; the package usually ships a larger logo that already sits on a tile.
/// </summary>
public static class PackageLogo
{
    /// <summary>Opaque pixels must cover this share of the visible area for an image to count as a tile.</summary>
    private const double PlatedFill = 0.8;

    /// <summary>Plated assets smaller than this look blurry on a key; a larger glyph on a drawn plate looks better.</summary>
    private const int MinPlatedSize = 96;

    /// <summary>Share of opaque pixels that may be coloured before an image stops counting as a one-colour glyph.</summary>
    private const double GlyphColourShare = 0.05;

    /// <summary>Tiles darker than this vanish on the black key background.</summary>
    private const float MinPlateBrightness = 0.1f;

    /// <summary>Plate colour is sampled this fraction (1/n) inside each edge, past any rounded corner or border.</summary>
    private const int PlateSampleInset = 20;

    private const int ColourSpread = 40;
    private const byte OpaqueAlpha = 128;
    private const int PlateSize = 256;
    private const float GlyphShare = 0.45f;

    /// <summary>Plate side as a share of the image, leaving the margin Store logos have.</summary>
    private const float PlateShare = 0.75f;
    private const float PlateCorner = 0.22f;

    private const string AppIconAttribute = "Square44x44Logo";
    private const string StoreLogoAttribute = "Logo";
    private static readonly string[] LogoAttributes = [AppIconAttribute, "Square150x150Logo", "Square310x310Logo", "Square71x71Logo"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Null when the app isn't installed or its assets can't be read; callers then fall back to the shell.</summary>
    public static Bitmap? Load(string aumid)
    {
        var bang = aumid.IndexOf('!');
        if (bang <= 0)
            return null;
        var installDir = InstallDir(aumid[..bang]);
        return installDir is null ? null : FromPackage(installDir, aumid[(bang + 1)..]);
    }

    /// <summary>
    /// The first usable asset in Windows' own order: app-list icons, Store logo, scaled app icons, Start tiles; the
    /// largest of its kind. A one-colour glyph is only used when nothing else is, drawn on a plate in the tile colour.
    /// </summary>
    public static Bitmap? FromPackage(string installDir, string appId)
    {
        var manifest = Path.Combine(installDir, "AppxManifest.xml");
        if (!File.Exists(manifest))
            return null;
        XDocument doc;
        try
        {
            doc = XDocument.Load(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == appId);
        var visual = app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        if (visual is null)
            return null;

        var logos = LogoAttributes
            .Select(a => (Attribute: a, Path: (string?)visual.Attribute(a) ?? (string?)visual.Descendants().FirstOrDefault(e => e.Attribute(a) is not null)?.Attribute(a)))
            .Append((Attribute: StoreLogoAttribute, Path: doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties")?.Value))
            .Where(l => l.Path is not null);

        var candidates = logos
            .SelectMany(l => AssetFiles(installDir, l.Path!).Select(file => (l.Attribute, File: file)))
            .DistinctBy(c => c.File, StringComparer.OrdinalIgnoreCase)
            .Select(c => Measure(c.File, KindOf(c.Attribute, c.File)))
            .OfType<Asset>()
            .ToList();
        try
        {
            var usable = candidates.Where(c => !c.DarkPlate && !c.Glyph && (c.Plated || !c.LightTheme) && c.Size >= MinPlatedSize);
            var chosen = usable.GroupBy(c => c.Kind).OrderBy(g => g.Key).Select(g => g.MaxBy(c => c.Size)).FirstOrDefault();
            if (chosen is not null)
                return Take(chosen);

            var glyph = candidates.Where(c => c.Glyph && !c.LightTheme).MaxBy(c => c.Size);
            if (glyph is not null)
                return OnPlate(glyph.Image, PlateColour((string?)visual.Attribute("BackgroundColor")));

            var largest = candidates.Where(c => !c.LightTheme).MaxBy(c => c.Size);
            return largest is null ? null : Take(largest);
        }
        finally
        {
            foreach (var c in candidates)
                c.Image.Dispose();
        }
    }

    /// <summary>Asset kinds in the order Windows prefers them for an app's icon.</summary>
    internal enum AssetKind
    {
        AppIcon,
        StoreLogo,
        ScaledAppIcon,
        Tile,
    }

    internal static AssetKind KindOf(string attribute, string file) => attribute switch
    {
        AppIconAttribute when Path.GetFileName(file).Contains(".targetsize-", StringComparison.OrdinalIgnoreCase) => AssetKind.AppIcon,
        AppIconAttribute => AssetKind.ScaledAppIcon,
        StoreLogoAttribute => AssetKind.StoreLogo,
        _ => AssetKind.Tile,
    };

    /// <summary>
    /// Files for a manifest logo path: the path itself and its resource variants, such as
    /// <c>Logo.scale-200.png</c> or <c>Logo.targetsize-256_altform-lightunplated.png</c>. High-contrast variants are skipped.
    /// </summary>
    internal static IEnumerable<string> AssetFiles(string installDir, string logo)
    {
        var path = Path.Combine(installDir, logo.Replace('/', '\\'));
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return [];
        var stem = Path.GetFileNameWithoutExtension(path);
        try
        {
            return Directory.EnumerateFiles(dir, stem + "*")
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return (name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                        && !name.Contains("contrast-", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record Asset(Bitmap Image, AssetKind Kind, int Size, bool Plated, bool Glyph, bool DarkPlate, bool LightTheme);

    private static Asset? Measure(string file, AssetKind kind)
    {
        var image = IconExtractor.LoadImageFile(file);
        if (image is null)
            return null;
        if (image.Width != image.Height)
        {
            image.Dispose();
            return null;
        }
        var (plated, glyph, darkPlate) = Shape(image);
        var lightTheme = Path.GetFileName(file).Contains("lightunplated", StringComparison.OrdinalIgnoreCase);
        return new Asset(image, kind, image.Width, plated, glyph, darkPlate, lightTheme);
    }

    /// <summary>
    /// Whether the visible part is a filled tile, whether it is a one-colour glyph, and whether its tile is too dark
    /// to see on a black key.
    /// </summary>
    internal static (bool Plated, bool Glyph, bool DarkPlate) Shape(Bitmap image)
    {
        var bounds = KeyRenderer.ContentBounds(image);
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Width * 4];
            long opaque = 0, coloured = 0;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = bounds.Left; x < bounds.Right; x++)
                {
                    if (row[x * 4 + 3] < OpaqueAlpha)
                        continue;
                    opaque++;
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > ColourSpread)
                        coloured++;
                }
            }
            var area = (double)bounds.Width * bounds.Height;
            var plated = area > 0 && opaque / area >= PlatedFill;
            var glyph = opaque > 0 && !plated && coloured < opaque * GlyphColourShare;
            var darkPlate = plated && PlateBrightness(data, bounds) < MinPlateBrightness;
            return (plated, glyph, darkPlate);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    /// <summary>Average brightness just inside the middle of each edge of the visible area, where a tile shows its own colour.</summary>
    private static float PlateBrightness(BitmapData data, Rectangle bounds)
    {
        var insetX = Math.Max(1, bounds.Width / PlateSampleInset);
        var insetY = Math.Max(1, bounds.Height / PlateSampleInset);
        (int X, int Y)[] points =
        [
            (bounds.Left + insetX, bounds.Top + bounds.Height / 2),
            (bounds.Right - 1 - insetX, bounds.Top + bounds.Height / 2),
            (bounds.Left + bounds.Width / 2, bounds.Top + insetY),
            (bounds.Left + bounds.Width / 2, bounds.Bottom - 1 - insetY),
        ];
        var pixel = new byte[4];
        var total = 0f;
        foreach (var (x, y) in points)
        {
            Marshal.Copy(data.Scan0 + y * data.Stride + x * 4, pixel, 0, 4);
            total += Color.FromArgb(pixel[2], pixel[1], pixel[0]).GetBrightness();
        }
        return total / points.Length;
    }

    private static Bitmap Take(Asset asset) => new(asset.Image);

    /// <summary>The tile colour from the manifest; null for <c>transparent</c>, a name it can't read, or one too dark to see on a key.</summary>
    internal static Color? PlateColour(string? backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor) || backgroundColor.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        Color colour;
        if (backgroundColor.StartsWith('#') && backgroundColor.Length == 7
            && int.TryParse(backgroundColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            colour = Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
        else
        {
            colour = Color.FromName(backgroundColor);
            if (!colour.IsKnownColor)
                return null;
        }
        return colour.GetBrightness() < 0.1f ? null : colour;
    }

    /// <summary>The glyph centred on a rounded tile: the manifest colour, or the control-key tile when there is none.</summary>
    internal static Bitmap OnPlate(Bitmap glyph, Color? colour)
    {
        var canvas = new Bitmap(PlateSize, PlateSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var plate = PlateSize * PlateShare;
        var box = new RectangleF((PlateSize - plate) / 2, (PlateSize - plate) / 2, plate, plate);
        if (colour is { } c)
        {
            using var path = IconArt.RoundRect(box, plate * PlateCorner);
            using var brush = new SolidBrush(c);
            g.FillPath(brush, path);
        }
        else
        {
            IconArt.DrawControlTile(g, box);
        }

        var source = KeyRenderer.ContentBounds(glyph);
        var scale = PlateSize * GlyphShare / Math.Max(source.Width, source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        g.DrawImage(glyph, new RectangleF((PlateSize - width) / 2, (PlateSize - height) / 2, width, height), source, GraphicsUnit.Pixel);
        return canvas;
    }

    private static string? InstallDir(string family)
    {
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref length, IntPtr.Zero) != ErrorInsufficientBuffer || count == 0)
            return null;
        var names = new IntPtr[count];
        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0)
                return null;
            foreach (var name in names)
            {
                var fullName = Marshal.PtrToStringUni(name);
                if (fullName is null)
                    continue;
                uint pathLength = 0;
                if (GetPackagePathByFullName(fullName, ref pathLength, null) != ErrorInsufficientBuffer)
                    continue;
                var path = new StringBuilder((int)pathLength);
                if (GetPackagePathByFullName(fullName, ref pathLength, path) == 0 && Directory.Exists(path.ToString()))
                    return path.ToString();
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr[]? fullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);
}
