using System.Drawing;
using System.Drawing.Imaging;
using ManagedLauncher.Core.Icons;

namespace ManagedLauncher.Tests;

public class PackageLogoTests
{
    private const string AppId = "App";
    private static readonly Color TileBlue = Color.FromArgb(0x00, 0x78, 0xD4);

    private static void Manifest(TempDir dir, string backgroundColor = "transparent") =>
        File.WriteAllText(dir.File("AppxManifest.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Properties><Logo>Assets\StoreLogo.png</Logo></Properties>
              <Applications>
                <Application Id="{AppId}">
                  <uap:VisualElements BackgroundColor="{backgroundColor}" Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Square150x150Logo.png" />
                </Application>
              </Applications>
            </Package>
            """);

    private static void Asset(TempDir dir, string name, int size, Action<Graphics, int> draw)
    {
        Directory.CreateDirectory(dir.File("Assets"));
        using var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image))
            draw(g, size);
        image.Save(dir.File(Path.Combine("Assets", name)), ImageFormat.Png);
    }

    private static void ColourCircle(Graphics g, int size)
    {
        using var brush = new SolidBrush(Color.Red);
        g.FillEllipse(brush, size / 8, size / 8, size * 3 / 4, size * 3 / 4);
    }

    private static void WhiteGlyph(Graphics g, int size)
    {
        g.FillRectangle(Brushes.White, size / 4, size / 4, size / 4, size / 4);
        g.FillRectangle(Brushes.White, size / 2, size / 2, size / 4, size / 4);
    }

    private static Action<Graphics, int> Tile(Color plate, Color mark) => (g, size) =>
    {
        using var brush = new SolidBrush(plate);
        g.FillRectangle(brush, 0, 0, size, size);
        using var markBrush = new SolidBrush(mark);
        g.FillRectangle(markBrush, size * 7 / 16, size * 7 / 16, size / 8, size / 8);
    };

    [Fact]
    public void AppListIconWinsOverLargerStartTile()
    {
        using var dir = new TempDir();
        Manifest(dir);
        Asset(dir, "Square44x44Logo.targetsize-256.png", 256, ColourCircle);
        Asset(dir, "Square150x150Logo.scale-400.png", 600, Tile(Color.White, Color.Green));

        using var logo = PackageLogo.FromPackage(dir.Path, AppId);

        Assert.NotNull(logo);
        Assert.Equal(256, logo.Width);
        Assert.Equal(Color.Red.ToArgb(), logo.GetPixel(128, 128).ToArgb());
    }

    [Fact]
    public void GlyphAndBlackTileGiveWayToStoreLogo()
    {
        using var dir = new TempDir();
        Manifest(dir, "#000000");
        Asset(dir, "Square44x44Logo.targetsize-256_altform-unplated.png", 256, WhiteGlyph);
        Asset(dir, "Square150x150Logo.scale-400.png", 600, Tile(Color.Black, Color.White));
        Asset(dir, "StoreLogo.scale-400.png", 200, Tile(Color.FromArgb(0x40, 0x40, 0x40), Color.White));

        using var logo = PackageLogo.FromPackage(dir.Path, AppId);

        Assert.NotNull(logo);
        Assert.Equal(200, logo.Width);
    }

    [Fact]
    public void LoneGlyphIsDrawnOnTileColour()
    {
        using var dir = new TempDir();
        Manifest(dir, "#0078D4");
        Asset(dir, "Square44x44Logo.targetsize-256.png", 256, WhiteGlyph);

        using var logo = PackageLogo.FromPackage(dir.Path, AppId);

        Assert.NotNull(logo);
        Assert.Equal(TileBlue.ToArgb(), logo.GetPixel(logo.Width / 2, logo.Height * 5 / 32).ToArgb());
        Assert.Equal(Color.White.ToArgb(), logo.GetPixel(logo.Width * 3 / 8, logo.Height * 3 / 8).ToArgb());
    }

    [Fact]
    public void HighContrastAssetsAreIgnored()
    {
        using var dir = new TempDir();
        Manifest(dir);
        Asset(dir, "Square44x44Logo.targetsize-256_contrast-white.png", 256, ColourCircle);

        Assert.Null(PackageLogo.FromPackage(dir.Path, AppId));
    }

    [Fact]
    public void UnknownAppOrMissingManifestGivesNothing()
    {
        using var dir = new TempDir();
        Assert.Null(PackageLogo.FromPackage(dir.Path, AppId));

        Manifest(dir);
        Asset(dir, "Square44x44Logo.targetsize-256.png", 256, ColourCircle);
        Assert.Null(PackageLogo.FromPackage(dir.Path, "Other"));
    }

    [Theory]
    [InlineData("transparent", null)]
    [InlineData("#000000", null)]
    [InlineData("#0078D4", "#0078D4")]
    [InlineData("red", "#FF0000")]
    [InlineData("notacolour", null)]
    public void TileColourReadsManifestValue(string value, string? expected)
    {
        var colour = PackageLogo.PlateColour(value);
        Assert.Equal(expected, colour is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null);
    }
}
