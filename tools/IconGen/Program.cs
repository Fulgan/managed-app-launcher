using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ManagedLauncher.Core.Icons;

// Writes every shipped icon from IconArt so they share one style:
//   dotnet run --project tools/IconGen -- <repo root>
var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var imgs = Path.Combine(root, "plugin", "imgs");
Directory.CreateDirectory(imgs);

Pair("plugin", 256, IconArt.Brand);
Pair("category", 28, size => IconArt.Monochrome(Glyph.Launcher, size));
Pair("action-open", 20, size => IconArt.Monochrome(Glyph.Launcher, size));
Pair("action-manage", 20, size => IconArt.Monochrome(Glyph.Gear, size));
Pair("action-slot", 20, size => IconArt.Monochrome(Glyph.Slot, size));
Pair("action-launch", 20, size => IconArt.Monochrome(Glyph.Play, size));
Key("key-open", () => IconArt.OpenKey(null));
Key("key-manage", () => IconArt.ControlKey(Glyph.Gear));
Key("key-launch", () => IconArt.ControlKey(Glyph.Play));
Key("key-slot", () => IconArt.ControlKey(Glyph.Plus, placeholder: true));
WriteIco(Path.Combine(root, "src", "ManagedLauncher.Manager", "app.ico"), [16, 20, 24, 32, 40, 48, 64, 128, 256]);

// Marketplace listing art: uploaded in Maker Console, not packaged with the plugin.
var marketplace = Path.Combine(root, "marketplace");
Directory.CreateDirectory(marketplace);
SaveTo(IconArt.Brand(288), Path.Combine(marketplace, "app-icon.png"));
SaveTo(Thumbnail(), Path.Combine(marketplace, "thumbnail.png"));
return 0;

void SaveTo(Bitmap bitmap, string path)
{
    using (bitmap)
        bitmap.Save(path, ImageFormat.Png);
    Console.WriteLine($"wrote {Path.GetRelativePath(root, path)}");
}

/// <summary>1920×960 banner: brand tile, name and tagline, and a row of the plugin's own keys.</summary>
static Bitmap Thumbnail()
{
    const int width = 1920, height = 960;
    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = IconArt.Prepare(bitmap);
    using (var background = new LinearGradientBrush(new Rectangle(0, 0, width, height), IconArt.TileBottom, Color.Black, LinearGradientMode.Vertical))
        g.FillRectangle(background, 0, 0, width, height);

    IconArt.DrawBrandTile(g, new RectangleF(170, 200, 400, 400));

    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using var title = new Font("Segoe UI Semibold", 96, FontStyle.Regular, GraphicsUnit.Pixel);
    using var tagline = new Font("Segoe UI", 44, FontStyle.Regular, GraphicsUnit.Pixel);
    using var ink = new SolidBrush(IconArt.Ink);
    using var muted = new SolidBrush(IconArt.Muted);
    g.DrawString("Managed App Launcher", title, ink, 660, 250);
    g.DrawString("App lists on every Stream Deck, paged for you,", tagline, muted, 668, 410);
    g.DrawString("with a running indicator and long press to close.", tagline, muted, 668, 468);

    Bitmap[] keys = [IconArt.OpenKey("Games"), IconArt.ControlKey(Glyph.Play), IconArt.ControlKey(Glyph.Gear), IconArt.ControlKey(Glyph.Plus, placeholder: true)];
    const int keySize = 160, gap = 40, pad = 24, radius = 36;
    // Keys are drawn on black, so they sit on a black panel shaped like a deck surface.
    var panel = new RectangleF(668 - pad, 640 - pad, keys.Length * keySize + (keys.Length - 1) * gap + 2 * pad, keySize + 2 * pad);
    using (var path = new GraphicsPath())
    using (var black = new SolidBrush(Color.Black))
    {
        path.AddArc(panel.Left, panel.Top, 2 * radius, 2 * radius, 180, 90);
        path.AddArc(panel.Right - 2 * radius, panel.Top, 2 * radius, 2 * radius, 270, 90);
        path.AddArc(panel.Right - 2 * radius, panel.Bottom - 2 * radius, 2 * radius, 2 * radius, 0, 90);
        path.AddArc(panel.Left, panel.Bottom - 2 * radius, 2 * radius, 2 * radius, 90, 90);
        path.CloseFigure();
        g.FillPath(black, path);
    }
    var x = 668f;
    foreach (var key in keys)
        using (key)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(key, x, 640, keySize, keySize);
            x += keySize + gap;
        }
    return bitmap;
}

void Pair(string name, int size, Func<int, Bitmap> draw)
{
    Save(draw(size), $"{name}.png");
    Save(draw(size * 2), $"{name}@2x.png");
}

void Key(string name, Func<Bitmap> draw)
{
    using var full = draw();
    Save(Scale(full, KeyRenderer.Size / 2), $"{name}.png");
    Save((Bitmap)full.Clone(), $"{name}@2x.png");
}

void Save(Bitmap bitmap, string file)
{
    using (bitmap)
        bitmap.Save(Path.Combine(imgs, file), ImageFormat.Png);
    Console.WriteLine($"wrote plugin/imgs/{file}");
}

static Bitmap Scale(Bitmap source, int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.DrawImage(source, 0, 0, size, size);
    return bitmap;
}

/// <summary>Multi-size .ico with PNG-compressed entries (supported since Windows Vista).</summary>
static void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(size =>
    {
        using var bitmap = IconArt.Brand(size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }).ToList();

    using var writer = new BinaryWriter(File.Create(path));
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        writer.Write(image);
    Console.WriteLine($"wrote {Path.GetRelativePath(Directory.GetCurrentDirectory(), path)}");
}
