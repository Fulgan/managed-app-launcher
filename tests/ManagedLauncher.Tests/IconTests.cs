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

    [Fact]
    public void TransparentPaddingIsCroppedSoIconFillsKey()
    {
        using var padded = new Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(padded))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.Blue, 112, 112, 32, 32);
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
