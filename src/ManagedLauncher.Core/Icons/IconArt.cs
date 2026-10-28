using System.Drawing;
using System.Drawing.Drawing2D;

namespace ManagedLauncher.Core.Icons;

public enum Glyph { Launcher, Return, Next, Gear, Play, Plus, Slot, Question, Folder, Star, Home, Games, Media, Music, Work, Code, Tools, Web, Chat, Graphics }

/// <summary>
/// The one drawing vocabulary for every icon the plugin and the manager ship or render: the brand tile (blue
/// squircle with a key grid and a running dot), the dark control tile, and line glyphs sized from their box so
/// they keep the same weight at every size.
/// </summary>
public static class IconArt
{
    public static readonly Color Ink = Color.FromArgb(0xF2, 0xF2, 0xF7);
    public static readonly Color MonochromeInk = Color.FromArgb(0xEF, 0xEF, 0xEF);
    public static readonly Color Muted = Color.FromArgb(0x8E, 0x8E, 0x93);
    public static readonly Color Faint = Color.FromArgb(0x63, 0x63, 0x66);
    public static readonly Color TileTop = Color.FromArgb(0x48, 0x48, 0x4C);
    public static readonly Color TileBottom = Color.FromArgb(0x26, 0x26, 0x29);
    public static readonly Color PlaceholderFill = Color.FromArgb(0x1C, 0x1C, 0x1E);
    public static readonly Color BrandTop = Color.FromArgb(0x4D, 0xA6, 0xFF);
    public static readonly Color BrandBottom = Color.FromArgb(0x00, 0x5B, 0xD6);
    public static readonly Color Green = Color.FromArgb(0x30, 0xD1, 0x58);
    public static readonly Color Red = Color.FromArgb(0xFF, 0x45, 0x3A);
    public static readonly Color Yellow = Color.FromArgb(0xFF, 0xD6, 0x0A);

    /// <summary>Squircle-like corner radius as a share of the tile side, as on iOS app icons.</summary>
    private const float TileCorner = 0.225f;

    /// <summary>Line weight as a share of the glyph box side.</summary>
    private const float Stroke = 0.13f;

    public static Graphics Prepare(Bitmap bitmap)
    {
        var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        return g;
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The brand mark: blue gradient squircle, white keys, the bottom-left one carrying a green running dot.</summary>
    public static void DrawBrandTile(Graphics g, RectangleF box)
    {
        using (var tile = RoundRect(box, box.Width * TileCorner))
        using (var gradient = new LinearGradientBrush(new PointF(0, box.Top), new PointF(0, box.Bottom), BrandTop, BrandBottom))
            g.FillPath(gradient, tile);

        var small = box.Width <= 24;
        int cols = small ? 2 : 3, rows = 2;
        var pad = box.Width * 0.2f;
        var gap = box.Width * (small ? 0.1f : 0.07f);
        var key = (box.Width - 2 * pad - (cols - 1) * gap) / cols;
        var top = box.Top + (box.Height - (rows * key + (rows - 1) * gap)) / 2;
        using var white = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var green = new SolidBrush(Green);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = new RectangleF(box.Left + pad + c * (key + gap), top + r * (key + gap), key, key);
                using (var path = RoundRect(cell, key * 0.24f))
                    g.FillPath(white, path);
                if (r == rows - 1 && c == 0)
                {
                    var dot = key * 0.36f;
                    g.FillEllipse(green, cell.X + (key - dot) / 2, cell.Y + (key - dot) / 2, dot, dot);
                }
            }
        }
    }

    /// <summary>The dark tile behind every control key (back, next page, manage, placeholders).</summary>
    public static void DrawControlTile(Graphics g, RectangleF box, bool placeholder = false)
    {
        using var path = RoundRect(box, box.Width * TileCorner);
        if (!placeholder)
        {
            using var gradient = new LinearGradientBrush(new PointF(0, box.Top), new PointF(0, box.Bottom), TileTop, TileBottom);
            g.FillPath(gradient, path);
            return;
        }
        using var fill = new SolidBrush(PlaceholderFill);
        g.FillPath(fill, path);
        using var dashed = new Pen(Faint, box.Width * 0.035f) { DashStyle = DashStyle.Dash, Alignment = PenAlignment.Inset };
        g.DrawPath(dashed, path);
    }

    /// <summary>Draws a glyph filling <paramref name="box"/> (square) in one colour.</summary>
    public static void DrawGlyph(Graphics g, Glyph glyph, RectangleF box, Color ink)
    {
        var s = box.Width;
        PointF P(float x, float y) => new(box.X + x * s, box.Y + y * s);
        using var pen = new Pen(ink, s * Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);

        switch (glyph)
        {
            case Glyph.Return:
                using (var arrow = new GraphicsPath())
                {
                    var r = 0.22f * s;
                    arrow.AddLine(P(0.2f, 0.36f), P(0.58f, 0.36f));
                    arrow.AddArc(box.X + 0.58f * s - r, box.Y + 0.36f * s, 2 * r, 2 * r, 270, 180);
                    arrow.AddLine(P(0.58f, 0.36f + 0.44f), P(0.36f, 0.8f));
                    g.DrawPath(pen, arrow);
                }
                g.DrawLines(pen, [P(0.36f, 0.2f), P(0.2f, 0.36f), P(0.36f, 0.52f)]);
                break;
            case Glyph.Next:
                g.DrawLines(pen, [P(0.38f, 0.18f), P(0.66f, 0.5f), P(0.38f, 0.82f)]);
                break;
            case Glyph.Plus:
                g.DrawLine(pen, P(0.5f, 0.2f), P(0.5f, 0.8f));
                g.DrawLine(pen, P(0.2f, 0.5f), P(0.8f, 0.5f));
                break;
            case Glyph.Slot:
                using (var frame = RoundRect(new RectangleF(box.X + s * 0.08f, box.Y + s * 0.08f, s * 0.84f, s * 0.84f), s * 0.2f))
                using (var dashed = new Pen(ink, s * 0.09f) { DashStyle = DashStyle.Dash })
                    g.DrawPath(dashed, frame);
                using (var thin = new Pen(ink, s * 0.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(thin, P(0.5f, 0.32f), P(0.5f, 0.68f));
                    g.DrawLine(thin, P(0.32f, 0.5f), P(0.68f, 0.5f));
                }
                break;
            case Glyph.Play:
                using (var triangle = new GraphicsPath())
                {
                    triangle.AddPolygon([P(0.3f, 0.2f), P(0.8f, 0.5f), P(0.3f, 0.8f)]);
                    using var soft = new Pen(ink, s * 0.08f) { LineJoin = LineJoin.Round };
                    g.FillPath(brush, triangle);
                    g.DrawPath(soft, triangle);
                }
                break;
            case Glyph.Gear:
                using (var gear = GearPath(box))
                    g.FillPath(brush, gear);
                break;
            case Glyph.Launcher:
                foreach (var cell in LauncherCells(box))
                {
                    using var path = RoundRect(cell, cell.Width * 0.26f);
                    g.FillPath(brush, path);
                }
                break;
            case Glyph.Question:
                using (var font = new Font(FontFamily.GenericSansSerif, s * 0.8f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("?", font, brush, box, format);
                break;
            default:
                DrawStockGlyph(g, glyph, box, ink);
                break;
        }
    }

    private static void DrawStockGlyph(Graphics g, Glyph glyph, RectangleF box, Color ink)
    {
        var s = box.Width;
        PointF P(float x, float y) => new(box.X + x * s, box.Y + y * s);
        RectangleF R(float x, float y, float w, float h) => new(box.X + x * s, box.Y + y * s, w * s, h * s);
        using var brush = new SolidBrush(ink);
        using var pen = new Pen(ink, s * Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thin = new Pen(ink, s * 0.08f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var edge = new Pen(ink, s * 0.06f) { LineJoin = LineJoin.Round };
        using var shape = new GraphicsPath(FillMode.Alternate);

        switch (glyph)
        {
            case Glyph.Folder:
                shape.AddPolygon([P(0.1f, 0.24f), P(0.4f, 0.24f), P(0.48f, 0.33f), P(0.9f, 0.33f), P(0.9f, 0.8f), P(0.1f, 0.8f)]);
                g.DrawPath(edge, shape);
                g.FillPath(brush, shape);
                break;
            case Glyph.Star:
                var star = new PointF[10];
                for (var i = 0; i < star.Length; i++)
                {
                    var r = i % 2 == 0 ? 0.42f : 0.18f;
                    var a = Math.PI * i / 5;
                    star[i] = P(0.5f + r * (float)Math.Sin(a), 0.53f - r * (float)Math.Cos(a));
                }
                shape.AddPolygon(star);
                g.DrawPath(edge, shape);
                g.FillPath(brush, shape);
                break;
            case Glyph.Home:
                shape.AddPolygon([P(0.5f, 0.14f), P(0.88f, 0.48f), P(0.78f, 0.48f), P(0.78f, 0.84f), P(0.22f, 0.84f), P(0.22f, 0.48f), P(0.12f, 0.48f)]);
                shape.AddRectangle(R(0.42f, 0.6f, 0.16f, 0.24f));
                g.DrawPolygon(edge, [P(0.5f, 0.14f), P(0.88f, 0.48f), P(0.12f, 0.48f)]);
                g.FillPath(brush, shape);
                break;
            case Glyph.Games:
                using (var body = RoundRect(R(0.08f, 0.28f, 0.84f, 0.46f), s * 0.2f))
                    shape.AddPath(body, false);
                shape.AddRectangle(R(0.215f, 0.46f, 0.17f, 0.06f));
                shape.AddRectangle(R(0.27f, 0.405f, 0.06f, 0.17f));
                shape.AddEllipse(R(0.62f, 0.38f, 0.09f, 0.09f));
                shape.AddEllipse(R(0.71f, 0.49f, 0.09f, 0.09f));
                g.FillPath(brush, shape);
                break;
            case Glyph.Media:
                using (var screen = RoundRect(R(0.1f, 0.2f, 0.8f, 0.6f), s * 0.12f))
                    shape.AddPath(screen, false);
                shape.AddPolygon([P(0.42f, 0.36f), P(0.64f, 0.5f), P(0.42f, 0.64f)]);
                g.FillPath(brush, shape);
                break;
            case Glyph.Music:
                g.FillEllipse(brush, R(0.16f, 0.62f, 0.24f, 0.2f));
                g.FillEllipse(brush, R(0.58f, 0.54f, 0.24f, 0.2f));
                g.DrawLine(thin, P(0.36f, 0.72f), P(0.36f, 0.26f));
                g.DrawLine(thin, P(0.78f, 0.64f), P(0.78f, 0.18f));
                shape.AddPolygon([P(0.32f, 0.2f), P(0.82f, 0.1f), P(0.82f, 0.24f), P(0.32f, 0.34f)]);
                g.FillPath(brush, shape);
                break;
            case Glyph.Work:
                using (var handle = RoundRect(R(0.36f, 0.16f, 0.28f, 0.2f), s * 0.06f))
                    g.DrawPath(thin, handle);
                using (var bag = RoundRect(R(0.1f, 0.3f, 0.8f, 0.52f), s * 0.1f))
                    shape.AddPath(bag, false);
                shape.AddRectangle(R(0.1f, 0.5f, 0.8f, 0.05f));
                g.FillPath(brush, shape);
                break;
            case Glyph.Code:
                g.DrawLines(pen, [P(0.3f, 0.28f), P(0.1f, 0.5f), P(0.3f, 0.72f)]);
                g.DrawLines(pen, [P(0.7f, 0.28f), P(0.9f, 0.5f), P(0.7f, 0.72f)]);
                g.DrawLine(thin, P(0.58f, 0.2f), P(0.42f, 0.8f));
                break;
            case Glyph.Tools:
                using (var handle = new Pen(ink, s * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(handle, P(0.18f, 0.82f), P(0.6f, 0.4f));
                shape.AddEllipse(R(0.42f, 0.1f, 0.48f, 0.48f));
                using (var jaw = new GraphicsPath())
                using (var head = new Region(shape))
                {
                    jaw.AddPolygon([P(0.6f, 0.28f), P(0.72f, 0.4f), P(1f, 0.12f), P(0.88f, 0f)]);
                    head.Exclude(jaw);
                    g.FillRegion(brush, head);
                }
                break;
            case Glyph.Web:
                g.DrawEllipse(thin, R(0.12f, 0.12f, 0.76f, 0.76f));
                g.DrawEllipse(thin, R(0.33f, 0.12f, 0.34f, 0.76f));
                g.DrawLine(thin, P(0.12f, 0.5f), P(0.88f, 0.5f));
                g.DrawLine(thin, P(0.2f, 0.3f), P(0.8f, 0.3f));
                g.DrawLine(thin, P(0.2f, 0.7f), P(0.8f, 0.7f));
                break;
            case Glyph.Chat:
                using (var bubble = RoundRect(R(0.1f, 0.16f, 0.8f, 0.54f), s * 0.16f))
                    g.FillPath(brush, bubble);
                shape.AddPolygon([P(0.26f, 0.64f), P(0.46f, 0.64f), P(0.22f, 0.86f)]);
                g.FillPath(brush, shape);
                break;
            case Glyph.Graphics:
                shape.AddEllipse(R(0.1f, 0.14f, 0.8f, 0.72f));
                shape.AddEllipse(R(0.52f, 0.56f, 0.18f, 0.18f));
                shape.AddEllipse(R(0.24f, 0.32f, 0.12f, 0.12f));
                shape.AddEllipse(R(0.42f, 0.22f, 0.12f, 0.12f));
                shape.AddEllipse(R(0.62f, 0.3f, 0.12f, 0.12f));
                shape.AddEllipse(R(0.22f, 0.52f, 0.12f, 0.12f));
                g.FillPath(brush, shape);
                break;
        }
    }

    /// <summary>Key cells of the launcher glyph in reading order: 3×2, or 2×2 when tiny.</summary>
    private static RectangleF[] LauncherCells(RectangleF box)
    {
        var s = box.Width;
        var small = s <= 24;
        int cols = small ? 2 : 3, rows = 2;
        var gap = s * (small ? 0.14f : 0.1f);
        var key = (s - (cols - 1) * gap) / cols;
        var top = box.Y + (s - (rows * key + (rows - 1) * gap)) / 2;
        var cells = new RectangleF[cols * rows];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                cells[r * cols + c] = new RectangleF(box.X + c * (key + gap), top + r * (key + gap), key, key);
        return cells;
    }

    /// <summary>Eight-tooth gear with a centre hole; even-odd fill leaves the hole transparent.</summary>
    private static GraphicsPath GearPath(RectangleF box)
    {
        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        float outer = box.Width * 0.48f, inner = box.Width * 0.36f, hole = box.Width * 0.15f;
        const int teeth = 8;
        var points = new List<PointF>();
        for (var i = 0; i < teeth; i++)
        {
            var a = i * 2 * Math.PI / teeth;
            var half = Math.PI / teeth;
            double[] angles = [a - half * 0.95, a - half * 0.45, a + half * 0.45, a + half * 0.95];
            float[] radii = [inner, outer, outer, inner];
            for (var k = 0; k < 4; k++)
                points.Add(new PointF(cx + (float)(Math.Sin(angles[k]) * radii[k]), cy - (float)(Math.Cos(angles[k]) * radii[k])));
        }
        var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(points.ToArray());
        path.AddEllipse(cx - hole, cy - hole, hole * 2, hole * 2);
        return path;
    }

    private static readonly RectangleF KeyTile = new((KeyRenderer.Size - KeyRenderer.TileIconSize) / 2f, (KeyRenderer.Size - KeyRenderer.TileIconSize) / 2f, KeyRenderer.TileIconSize, KeyRenderer.TileIconSize);

    public static Bitmap GlyphTile(Glyph glyph, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Transparent);
        DrawControlTile(g, new RectangleF(0, 0, size, size));
        var glyphSize = size * 62f / KeyRenderer.TileIconSize;
        DrawGlyph(g, glyph, new RectangleF((size - glyphSize) / 2, (size - glyphSize) / 2, glyphSize, glyphSize), Ink);
        return bitmap;
    }

    /// <summary>A 144 px control key: tile with a centred glyph.</summary>
    public static Bitmap ControlKey(Glyph glyph, bool placeholder = false)
    {
        const int size = KeyRenderer.Size;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Black);
        DrawControlTile(g, KeyTile, placeholder);
        const float glyphSize = 62;
        DrawGlyph(g, glyph, new RectangleF(72 - glyphSize / 2, 72 - glyphSize / 2, glyphSize, glyphSize), placeholder ? Muted : Ink);
        return bitmap;
    }

    /// <summary>Most pages shown as dots; beyond that the key shows "page/pages" instead.</summary>
    public const int MaxPageDots = 7;

    /// <summary>Next-page key: chevron above a page indicator, dots (current one bright) or "page/pages".</summary>
    /// <param name="page">Zero-based page shown.</param>
    public static Bitmap NextPageKey(int page, int pages)
    {
        const int size = KeyRenderer.Size;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Black);
        DrawControlTile(g, KeyTile);
        const float glyphSize = 52, glyphCenterY = 62;
        DrawGlyph(g, Glyph.Next, new RectangleF(72 - glyphSize / 2, glyphCenterY - glyphSize / 2, glyphSize, glyphSize), Ink);

        const float rowY = 108;
        if (pages <= MaxPageDots)
        {
            const float dot = 9, gap = 7;
            var left = 72 - (pages * dot + (pages - 1) * gap) / 2;
            using var current = new SolidBrush(Ink);
            using var other = new SolidBrush(Faint);
            for (var i = 0; i < pages; i++)
                g.FillEllipse(i == page ? current : other, left + i * (dot + gap), rowY - dot / 2, dot, dot);
            return bitmap;
        }
        using var font = new Font("Segoe UI Semibold", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Muted);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString($"{page + 1}/{pages}", font, ink, new RectangleF(14, rowY - 12, 116, 24), format);
        return bitmap;
    }

    /// <summary>
    /// Open Launcher key: the control tile with the key-grid glyph, its bottom-left key carrying the green
    /// running dot of the brand mark, and the list name drawn under it. Null <paramref name="list"/> leaves the
    /// grid centred with no label.
    /// </summary>
    public static Bitmap OpenKey(string? list, Glyph glyph = Glyph.Launcher)
    {
        const int size = KeyRenderer.Size;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Black);
        DrawControlTile(g, KeyTile);
        var labelled = !string.IsNullOrEmpty(list);
        var glyphSize = labelled ? 52f : 62f;
        var centerY = labelled ? 60f : 72f;
        var box = new RectangleF(72 - glyphSize / 2, centerY - glyphSize / 2, glyphSize, glyphSize);
        DrawGlyph(g, glyph, box, Ink);
        if (glyph == Glyph.Launcher)
        {
            var bottomLeft = LauncherCells(box)[3];
            var dot = bottomLeft.Width * 0.42f;
            using var green = new SolidBrush(Green);
            g.FillEllipse(green, bottomLeft.X + (bottomLeft.Width - dot) / 2, bottomLeft.Y + (bottomLeft.Height - dot) / 2, dot, dot);
        }
        if (labelled)
            DrawListLabel(g, list!);
        return bitmap;
    }

    public static void DrawListLabel(Graphics g, string list)
    {
        using var font = new Font("Segoe UI Semibold", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Muted);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(list, font, ink, new RectangleF(22, 96, 100, 24), format);
    }

    /// <summary>Square transparent icon of one monochrome glyph, as Stream Deck expects for action list and category icons.</summary>
    public static Bitmap Monochrome(Glyph glyph, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Transparent);
        var margin = size * 0.1f;
        DrawGlyph(g, glyph, new RectangleF(margin, margin, size - 2 * margin, size - 2 * margin), MonochromeInk);
        return bitmap;
    }

    /// <summary>Transparent square with the brand tile, for the plugin and app icons.</summary>
    public static Bitmap Brand(int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Prepare(bitmap);
        g.Clear(Color.Transparent);
        var margin = Math.Max(1, MathF.Round(size * 0.04f));
        DrawBrandTile(g, new RectangleF(margin, margin, size - 2 * margin, size - 2 * margin));
        return bitmap;
    }
}
