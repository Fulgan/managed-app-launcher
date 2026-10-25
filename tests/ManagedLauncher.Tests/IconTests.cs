using System.Drawing;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;

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

        using var running = KeyRenderer.Render(icon, new KeyVisual("x", true, IndicatorStyle.Border, false));
        using var stopped = KeyRenderer.Render(icon, new KeyVisual("x", false, IndicatorStyle.Border, false));

        Assert.Equal(KeyRenderer.Size, running.Width);
        Assert.Equal(KeyRenderer.RunningColor.ToArgb(), running.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), stopped.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
    }

    [Fact]
    public void RunningDotPaintsBottomLeftAndLeavesEdge()
    {
        using var icon = SolidIcon();

        using var bmp = KeyRenderer.Render(icon, new KeyVisual("x", true, IndicatorStyle.Dot, false));

        Assert.Equal(KeyRenderer.RunningColor.ToArgb(), bmp.GetPixel(20, KeyRenderer.Size - 20).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bmp.GetPixel(2, KeyRenderer.Size / 2).ToArgb());
    }

    [Fact]
    public void IconIsDrawnInCentre()
    {
        using var icon = SolidIcon();

        using var bmp = KeyRenderer.Render(icon, new KeyVisual("x", false, IndicatorStyle.Border, false));

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

        using var bmp = KeyRenderer.Render(padded, new KeyVisual("x", false, IndicatorStyle.Dot, false));

        Assert.Equal(Color.Blue.ToArgb(), bmp.GetPixel(14, KeyRenderer.Size / 2).ToArgb());
        Assert.Equal(new Rectangle(112, 112, 32, 32), KeyRenderer.ContentBounds(padded));
    }

    [Fact]
    public void RenderPngProducesPng()
    {
        var png = KeyRenderer.RenderPng(null, new KeyVisual(null, false, IndicatorStyle.Border, true));

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public void EqualVisualsCompareEqual()
    {
        Assert.Equal(new KeyVisual("a", true, IndicatorStyle.Dot, false), new KeyVisual("a", true, IndicatorStyle.Dot, false));
        Assert.NotEqual(new KeyVisual("a", true, IndicatorStyle.Dot, false), new KeyVisual("a", false, IndicatorStyle.Dot, false));
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
}
