using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Core.Icons;

/// <summary>What a key shows. Two equal visuals render identical images, so callers skip redraws when nothing changed.</summary>
public sealed record KeyVisual(string? IconSource, bool Running, IndicatorStyle Indicator, bool Broken);

/// <summary>Draws a 144×144 key image (the Stream Deck @2x size): the app icon plus the running indicator.</summary>
public static class KeyRenderer
{
    public const int Size = 144;
    public static readonly Color RunningColor = IconArt.Green;
    public static readonly Color BrokenColor = IconArt.Red;

    /// <summary>Largest side of the icon's visible content; leaves a margin so the indicator never covers much of it.</summary>
    public const int IconSize = 124;
    private const int BorderWidth = 8;
    private const int DotDiameter = 24;
    private const int DotMargin = 8;
    /// <summary>Pixels below this alpha are shadow or frame, not icon content.</summary>
    private const byte VisibleAlpha = 128;

    public static byte[] RenderPng(Image? icon, KeyVisual visual)
    {
        using var canvas = Render(icon, visual);
        using var stream = new MemoryStream();
        canvas.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static Bitmap Render(Image? icon, KeyVisual visual)
    {
        var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Black);

        if (icon is not null)
            DrawIcon(g, icon);
        else
            DrawPlaceholder(g, Centered(IconSize * 3 / 4, IconSize * 3 / 4));

        if (visual.Broken)
            DrawBroken(g);
        else if (visual.Running)
            DrawIndicator(g, visual.Indicator);
        return canvas;
    }

    /// <summary>
    /// Shell icons often sit in a larger transparent frame (a 32 px icon padded to 256). Drawing only the visible
    /// part, scaled to <see cref="IconSize"/>, makes every app fill its key the same way.
    /// </summary>
    private static void DrawIcon(Graphics g, Image icon)
    {
        var source = icon is Bitmap bitmap ? ContentBounds(bitmap) : new Rectangle(Point.Empty, icon.Size);
        var scale = (float)IconSize / Math.Max(source.Width, source.Height);
        var width = (int)Math.Round(source.Width * scale);
        var height = (int)Math.Round(source.Height * scale);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(icon, Centered(width, height), source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }

    internal static Rectangle ContentBounds(Bitmap bitmap)
    {
        var full = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        if (!Image.IsAlphaPixelFormat(bitmap.PixelFormat))
            return full;

        var data = bitmap.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Width * 4];
            int left = data.Width, top = data.Height, right = -1, bottom = -1;
            for (var y = 0; y < data.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < data.Width; x++)
                {
                    if (row[x * 4 + 3] < VisibleAlpha)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < 0 ? full : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>A plain black key, for launcher keys with no app on the current page.</summary>
    public static byte[] RenderBlankPng()
    {
        using var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
            g.Clear(Color.Black);
        using var stream = new MemoryStream();
        canvas.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Next-page key: a right chevron above the page indicator.</summary>
    /// <param name="page">Zero-based page shown.</param>
    public static byte[] RenderNextPagePng(int page, int pages) => Png(IconArt.NextPageKey(page, pages));

    /// <summary>Back key: a U-turn arrow, returning to the profile the launcher was opened from.</summary>
    public static byte[] RenderBackPng() => Png(IconArt.ControlKey(Glyph.Return));

    /// <summary>Open Launcher key labelled with its list.</summary>
    public static byte[] RenderOpenPng(string list) => Png(IconArt.OpenKey(list));

    /// <summary>Manage key: a gear, opening the manager window.</summary>
    public static byte[] RenderManagePng() => Png(IconArt.ControlKey(Glyph.Gear));

    /// <summary>Encodes and disposes the bitmap.</summary>
    public static byte[] Png(Bitmap bitmap)
    {
        using (bitmap)
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private static Rectangle Centered(int width, int height) => new((Size - width) / 2, (Size - height) / 2, width, height);

    private static void DrawIndicator(Graphics g, IndicatorStyle style)
    {
        using var brush = new SolidBrush(RunningColor);
        if (style == IndicatorStyle.Dot)
        {
            var rect = new Rectangle(DotMargin, Size - DotDiameter - DotMargin, DotDiameter, DotDiameter);
            using var outline = new Pen(Color.Black, 3);
            g.FillEllipse(brush, rect);
            g.DrawEllipse(outline, rect);
            return;
        }
        using var pen = new Pen(RunningColor, BorderWidth) { Alignment = PenAlignment.Inset };
        g.DrawRectangle(pen, 0, 0, Size - 1, Size - 1);
    }

    private static void DrawBroken(Graphics g)
    {
        using var pen = new Pen(BrokenColor, 10) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, Size - 40, 12, Size - 12, 40);
        g.DrawLine(pen, Size - 12, 12, Size - 40, 40);
    }

    /// <summary>App whose icon can't be read: the control tile with a question mark.</summary>
    private static void DrawPlaceholder(Graphics g, Rectangle rect)
    {
        IconArt.DrawControlTile(g, rect);
        var glyph = rect.Width * 0.5f;
        IconArt.DrawGlyph(g, Glyph.Question, new RectangleF(rect.X + (rect.Width - glyph) / 2, rect.Y + (rect.Height - glyph) / 2, glyph, glyph), IconArt.Muted);
    }
}
