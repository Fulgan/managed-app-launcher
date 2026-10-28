using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ManagedLauncher.Core.Icons;

/// <param name="Index">Icon index in the file, for a <c>file,index</c> icon value; null when the file is an image.</param>
/// <param name="Value">Complete icon value, for choices that are not icons of one file (stock icons, apps).</param>
public sealed record IconChoice(int? Index, byte[] Png, string? Value = null, string? Name = null);

/// <summary>Every icon a file offers as a custom key icon: each icon of an exe, dll or .ico, or the image itself.</summary>
public static class IconCatalog
{
    public static readonly string WindowsIcons = Path.Combine(Environment.SystemDirectory, "imageres.dll");

    public static readonly IReadOnlySet<string> IconFileExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".ico", ".icl", ".cpl", ".mun" };

    public static readonly IReadOnlySet<string> ImageFileExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static bool IsIconFile(string file) => IconFileExtensions.Contains(Path.GetExtension(file));

    public static bool IsImageFile(string file) => ImageFileExtensions.Contains(Path.GetExtension(file));

    /// <summary>An icon value naming an app (exe, shortcut, packaged app) whose shell icon is shown, not one icon of a file.</summary>
    public static bool IsAppIcon(string icon)
    {
        var (file, index) = IconExtractor.ParseIconRef(icon);
        return index is null && StockIcons.Parse(icon) is null && !IsImageFile(file)
            && !Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<IconChoice> Stock(int size) =>
        StockIcons.All.Select(s =>
        {
            using var tile = IconArt.GlyphTile(s.Glyph, size);
            return new IconChoice(null, Png(tile, size), StockIcons.Ref(s.Name), s.Name);
        }).ToList();

    public static IReadOnlyList<IconChoice> Apps(IEnumerable<Config.Entry> entries, int size)
    {
        var choices = new List<IconChoice>();
        foreach (var entry in entries.Where(e => !e.IsBreak))
        {
            var value = AppIcon(entry);
            if (value is null || choices.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
                continue;
            using var icon = IconExtractor.LoadCustomIcon(Targets.TargetResolver.Expand(value));
            if (icon is not null)
                choices.Add(new IconChoice(null, Png(icon, size), value, entry.Title ?? Targets.AppNames.Default(entry)));
        }
        return choices;
    }

    public static string? AppIcon(Config.Entry entry) =>
        entry.Icon ?? Targets.TargetResolver.Resolve(entry).IconSource;

    /// <summary>Empty when the file is missing or holds no icon; icons that fail to extract are left out.</summary>
    public static IReadOnlyList<IconChoice> List(string file, int size)
    {
        if (!File.Exists(file))
            return [];
        if (!IsIconFile(file))
        {
            using var image = IconExtractor.LoadImageFile(file);
            return image is null ? [] : [new IconChoice(null, Png(image, size))];
        }

        var choices = new List<IconChoice>();
        var count = IconExtractor.CountIcons(file);
        for (var i = 0; i < count; i++)
        {
            using var icon = IconExtractor.ExtractIndexed(file, i, size);
            if (icon is not null)
                choices.Add(new IconChoice(i, Png(icon, size)));
        }
        return choices;
    }

    private static byte[] Png(Image image, int size)
    {
        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var scale = (float)size / Math.Max(image.Width, image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            g.DrawImage(image, (size - width) / 2, (size - height) / 2, width, height);
        }
        using var stream = new MemoryStream();
        canvas.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
