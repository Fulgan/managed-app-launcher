using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ManagedLauncher.Core.Icons;

// Writes every shipped icon from IconArt so they share one style:
//   dotnet run --project tools/IconGen -- <repo root>
var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var imgs = Path.Combine(root, "plugin", "imgs");
Directory.CreateDirectory(imgs);

Pair("plugin", 28, IconArt.Brand);
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
return 0;

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
