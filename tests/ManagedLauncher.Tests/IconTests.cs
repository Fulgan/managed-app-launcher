using System.Drawing;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Processes;

namespace ManagedLauncher.Tests;

public class IconTests
{
    private static Bitmap SolidIcon()
    {
        var icon = new Bitmap(32, 32);
        using var g = Graphics.FromImage(icon);
        g.Clear(Color.Blue);
        return icon;
    }

    [Fact]
    public void RunningBorderPaintsEdgeGreen()
    {
        using var icon = SolidIcon();

        using var running = KeyRenderer.Render(icon, new KeyVisual("x", RunState.Running, IndicatorStyle.Border, false));
        using var stopped = KeyRenderer.Render(icon, new KeyVisual("x", RunState.Stopped, IndicatorStyle.Border, false));

        Assert.Equal(KeyRenderer.Size, running.Width);
        Assert.Equal(KeyRenderer.RunningColor.ToArgb(), running.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), stopped.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
    }

    [Fact]
    public void RunningDotPaintsBottomLeftAndLeavesEdge()
    {
        using var icon = SolidIcon();

        using var bmp = KeyRenderer.Render(icon, new KeyVisual("x", RunState.Running, IndicatorStyle.Dot, false));

        Assert.Equal(KeyRenderer.RunningColor.ToArgb(), bmp.GetPixel(20, KeyRenderer.Size - 20).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bmp.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
    }

    [Fact]
    public void IconIsDrawnInCentre()
    {
        using var icon = SolidIcon();

        using var bmp = KeyRenderer.Render(icon, new KeyVisual("x", RunState.Stopped, IndicatorStyle.Border, false));

        Assert.Equal(Color.Blue.ToArgb(), bmp.GetPixel(KeyRenderer.Size / 2, KeyRenderer.Size / 2).ToArgb());
    }

    private static Bitmap Canvas(int size, Action<Graphics> draw)
    {
        var image = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Transparent);
        draw(g);
        return image;
    }

    private static Rectangle DrawnBounds(Bitmap key)
    {
        int left = key.Width, top = key.Height, right = -1, bottom = -1;
        for (var y = 0; y < key.Height; y++)
            for (var x = 0; x < key.Width; x++)
            {
                var p = key.GetPixel(x, y);
                if (p.R + p.G + p.B < 60)
                    continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static readonly KeyVisual Stopped = new("x", RunState.Stopped, IndicatorStyle.Dot, false);

    [Fact]
    public void OpenKeyIconSitsAboveListName()
    {
        using var circle = Canvas(256, g => g.FillEllipse(Brushes.Orange, 0, 0, 256, 256));

        using var key = new Bitmap(new MemoryStream(KeyRenderer.RenderOpenPng("games", circle)));
        using var plain = new Bitmap(new MemoryStream(KeyRenderer.RenderOpenPng("games")));

        var orange = Enumerable.Range(0, KeyRenderer.Size).SelectMany(y => Enumerable.Range(0, KeyRenderer.Size).Select(x => (x, y)))
            .Where(p => key.GetPixel(p.x, p.y) is { R: > 200, G: > 120 and < 200, B: < 60 }).ToList();
        Assert.InRange(orange.Max(p => p.y), 80, 95);
        Assert.InRange(orange.Max(p => p.x) - orange.Min(p => p.x), 70, 80);
        Assert.Equal(plain.GetPixel(72, 108), key.GetPixel(72, 108));
    }

    [Fact]
    public void SquareTileIsDrawnAtControlTileSize()
    {
        using var tile = Canvas(256, g => g.FillRectangle(Brushes.Orange, 0, 0, 256, 256));

        using var key = KeyRenderer.Render(tile, Stopped);

        Assert.InRange(DrawnBounds(key).Width, KeyRenderer.TileIconSize - 2, KeyRenderer.TileIconSize + 2);
    }

    [Fact]
    public void ShapedIconIsDrawnAtFullIconSize()
    {
        using var circle = Canvas(256, g => g.FillEllipse(Brushes.Orange, 0, 0, 256, 256));

        using var key = KeyRenderer.Render(circle, Stopped);

        Assert.InRange(DrawnBounds(key).Width, KeyRenderer.IconSize - 2, KeyRenderer.IconSize + 2);
    }

    [Fact]
    public void DesignedPaddingIsKept()
    {
        using var padded = Canvas(400, g => g.FillEllipse(Brushes.Orange, 60, 60, 280, 280));

        using var key = KeyRenderer.Render(padded, Stopped);

        Assert.InRange(DrawnBounds(key).Width, KeyRenderer.Size * 280 / 400 - 2, KeyRenderer.Size * 280 / 400 + 2);
    }

    [Fact]
    public void TransparentPaddingIsCroppedSoIconFillsKey()
    {
        using var padded = new Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(padded))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.Blue, 112, 112, 32, 16);
            g.FillRectangle(Brushes.Blue, 112, 128, 16, 16);
        }

        using var bmp = KeyRenderer.Render(padded, new KeyVisual("x", RunState.Stopped, IndicatorStyle.Dot, false));

        Assert.Equal(Color.Blue.ToArgb(), bmp.GetPixel(14, KeyRenderer.Size / 2).ToArgb());
        Assert.Equal(new Rectangle(112, 112, 32, 32), KeyRenderer.ContentBounds(padded));
    }

    [Fact]
    public void RenderPngProducesPng()
    {
        var png = KeyRenderer.RenderPng(null, new KeyVisual(null, RunState.Stopped, IndicatorStyle.Border, true));

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public void EqualVisualsCompareEqual()
    {
        Assert.Equal(new KeyVisual("a", RunState.Running, IndicatorStyle.Dot, false), new KeyVisual("a", RunState.Running, IndicatorStyle.Dot, false));
        Assert.NotEqual(new KeyVisual("a", RunState.Running, IndicatorStyle.Dot, false), new KeyVisual("a", RunState.Stopped, IndicatorStyle.Dot, false));
    }

    [Fact]
    public void ExtractsIconOfAnExeWithAlpha()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        using var icon = IconExtractor.Extract(notepad);

        Assert.NotNull(icon);
        Assert.True(icon.Width >= 32);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
    }

    [Fact]
    public void V54_ExtractsIconOfAnExeGivenWithForwardSlashes()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe").Replace('\\', '/');

        using var icon = IconExtractor.Extract(explorer);

        Assert.NotNull(icon);
    }

    [Fact]
    public void ExtractReturnsNullForMissingFile()
    {
        Assert.Null(IconExtractor.Extract(@"C:\definitely\not\here.exe"));
    }

    [Theory]
    [InlineData(@"C:\x\app.exe,3", @"C:\x\app.exe", 3)]
    [InlineData(@"C:\x\lib.dll,-102", @"C:\x\lib.dll", -102)]
    [InlineData(@"C:\x\pic.png", @"C:\x\pic.png", null)]
    [InlineData(@"C:\a,b\pic.png", @"C:\a,b\pic.png", null)]
    public void IconRefSplitsFileAndIndex(string icon, string file, int? index)
    {
        Assert.Equal((file, index), IconExtractor.ParseIconRef(icon));
    }

    [Fact]
    public void CustomIconWithoutIndexOnProgramUsesItsShellIcon()
    {
        using var icon = IconExtractor.LoadCustomIcon(Path.Combine(Environment.SystemDirectory, "notepad.exe"));

        Assert.NotNull(icon);
    }

    [Theory]
    [InlineData("stock:games", Glyph.Games)]
    [InlineData("STOCK: Folder ", Glyph.Folder)]
    [InlineData("stock:nope", null)]
    [InlineData(@"C:\x\games.ico", null)]
    [InlineData(null, null)]
    public void StockIconRefsParse(string? icon, Glyph? glyph)
    {
        Assert.Equal(glyph, StockIcons.Parse(icon));
    }

    [Fact]
    public void StockCatalogOffersEveryStockIconAsValue()
    {
        var choices = IconCatalog.Stock(48);

        Assert.Equal(StockIcons.All.Select(s => "stock:" + s.Name), choices.Select(c => c.Value));
        Assert.All(choices, c => Assert.Null(c.Index));
    }

    [Fact]
    public void StockOpenKeyDrawsItsGlyphInsteadOfLauncherGrid()
    {
        using var plain = new Bitmap(new MemoryStream(KeyRenderer.RenderOpenPng("games")));
        using var stock = new Bitmap(new MemoryStream(KeyRenderer.RenderOpenPng("games", Glyph.Games)));

        Assert.NotEqual(plain.GetPixel(72, 60), stock.GetPixel(72, 60));
        Assert.Equal(plain.GetPixel(72, 108), stock.GetPixel(72, 108));
    }

    [Fact]
    public void IndexedIconComesOutOfWindowsLibraryWithAlpha()
    {
        var library = Path.Combine(Environment.SystemDirectory, "imageres.dll");

        Assert.True(IconExtractor.CountIcons(library) > 10);
        using var icon = IconExtractor.LoadCustomIcon(library + ",3");

        Assert.NotNull(icon);
        Assert.Equal(256, icon.Width);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
    }
}
