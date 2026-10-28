using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;

namespace ManagedLauncher.Core.Icons;

/// <summary>What a key shows. Two equal visuals render identical images, so callers skip redraws when nothing changed.</summary>
public sealed record KeyVisual(string? IconSource, RunState State, IndicatorStyle Indicator, bool Broken);

/// <summary>Draws a 144×144 key image (the Stream Deck @2x size): the app icon plus the running indicator.</summary>
public static class KeyRenderer
{
    public const int Size = 144;
    public static readonly Color RunningColor = IconArt.Green;
    public static readonly Color ElevatedColor = IconArt.Red;
    public static readonly Color StartingColor = IconArt.Yellow;
    public static readonly Color BrokenColor = IconArt.Red;

    /// <summary>Largest side of the icon's visible content; leaves a margin so the indicator never covers much of it.</summary>
    public const int IconSize = 124;

    /// <summary>Side of an icon that is a filled square tile, which reads larger than a shaped icon; same as the control-key tile.</summary>
    public const int TileIconSize = 116;

    /// <summary>Opaque share of its visible area from which an icon counts as a filled tile.</summary>
    private const double TileFill = 0.85;

    /// <summary>Largest width/height difference, as a share of the longer side, for a shape to count as square.</summary>
    private const double SquareTolerance = 0.1;
    private const int BorderWidth = 8;
    private const int DotDiameter = 24;
    private const int DotMargin = 8;
    /// <summary>Content filling at least this share of its image is padded by design, not by an oversized frame.</summary>
    private const double DesignedPaddingFill = 0.6;

    private const int OpenIconFrame = 88;
    private const int OpenIconCenterY = 52;

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
            DrawIcon(g, icon, new PointF(Size / 2f, Size / 2f), Size);
        else
            DrawPlaceholder(g, Centered(IconSize * 3 / 4, IconSize * 3 / 4));

        if (visual.Broken)
            DrawBroken(g);
        else if (visual.State != RunState.Stopped)
            DrawIndicator(g, visual.Indicator, visual.State switch
            {
                RunState.Elevated => ElevatedColor,
                RunState.Starting => StartingColor,
                _ => RunningColor,
            });
        return canvas;
    }

    /// <summary>
    /// Shell icons often sit in a larger transparent frame (a 32 px icon padded to 256): only the visible part is
    /// drawn, scaled to <see cref="IconSize"/>. An image whose content already fills most of it keeps the padding
    /// its designer gave it, the whole image spanning the key, with the content never larger than <see cref="IconSize"/>.
    /// </summary>
    private static void DrawIcon(Graphics g, Image icon, PointF center, int frame)
    {
        var bitmap = icon as Bitmap;
        var source = bitmap is null ? new Rectangle(Point.Empty, icon.Size) : ContentBounds(bitmap);
        var content = Math.Max(source.Width, source.Height);
        var canvas = Math.Max(icon.Width, icon.Height);
        var target = (float)(bitmap is not null && IsSquareTile(bitmap, source) ? TileIconSize : IconSize) * frame / Size;
        var scale = target / content;
        if (content >= canvas * DesignedPaddingFill)
            scale = Math.Min(scale, (float)frame / canvas);
        var width = (int)Math.Round(source.Width * scale);
        var height = (int)Math.Round(source.Height * scale);
        var area = new Rectangle((int)Math.Round(center.X - width / 2f), (int)Math.Round(center.Y - height / 2f), width, height);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(icon, area, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>A filled, nearly square shape: it looks larger than a round or open icon of the same width.</summary>
    internal static bool IsSquareTile(Bitmap bitmap, Rectangle bounds) =>
        Math.Abs(bounds.Width - bounds.Height) <= Math.Max(bounds.Width, bounds.Height) * SquareTolerance
        && OpaqueShare(bitmap, bounds) >= TileFill;

    /// <summary>Share of pixels inside <paramref name="bounds"/> that are visible content.</summary>
    internal static double OpaqueShare(Bitmap bitmap, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return 0;
        if (!Image.IsAlphaPixelFormat(bitmap.PixelFormat))
            return 1;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Width * 4];
            long opaque = 0;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = bounds.Left; x < bounds.Right; x++)
                    if (row[x * 4 + 3] >= VisibleAlpha)
                        opaque++;
            }
            return opaque / ((double)bounds.Width * bounds.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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

    public static byte[] RenderOpenPng(string list, Glyph glyph) => Png(IconArt.OpenKey(list, glyph));

    public static byte[] RenderOpenPng(string list, Image? icon)
    {
        if (icon is null)
            return RenderOpenPng(list);
        var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Black);
            DrawIcon(g, icon, new PointF(Size / 2f, OpenIconCenterY), OpenIconFrame);
            IconArt.DrawListLabel(g, list);
        }
        return Png(canvas);
    }

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

    private static void DrawIndicator(Graphics g, IndicatorStyle style, Color color)
    {
        using var brush = new SolidBrush(color);
        if (style == IndicatorStyle.Dot)
        {
            var rect = new Rectangle(DotMargin, Size - DotDiameter - DotMargin, DotDiameter, DotDiameter);
            using var outline = new Pen(Color.Black, 3);
            g.FillEllipse(brush, rect);
            g.DrawEllipse(outline, rect);
            return;
        }
        using var pen = new Pen(color, BorderWidth) { Alignment = PenAlignment.Inset };
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
