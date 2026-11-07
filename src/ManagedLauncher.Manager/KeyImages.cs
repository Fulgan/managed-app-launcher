using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Manager;

/// <summary>Renders the exact key image the plugin shows, for previews in the manager.</summary>
public static class KeyImages
{
    public static ImageSource Render(Entry entry, AppConfig config, RunState state = RunState.Stopped)
    {
        var target = TargetResolver.Resolve(entry);
        var options = LaunchOptions.Resolve(entry);
        var source = entry.Icon is { } custom ? TargetResolver.Expand(custom) : target.IconSource;
        using var icon = source is null ? null
            : entry.Icon is not null ? IconExtractor.LoadCustomIcon(source)
            : IconExtractor.ExtractBest(source);
        var png = KeyRenderer.RenderPng(icon, new KeyVisual(source, state, options.Indicator, !target.IsLaunchable));
        return ToImage(png);
    }

    /// <summary>An Open Launcher key for the list: its icon, else the launcher glyph (labelled only when the theme paints it).</summary>
    public static ImageSource RenderOpen(string list, string? icon, KeyTheme theme)
    {
        if (icon is null)
            return ToImage(KeyRenderer.Png(IconArt.OpenKey(theme == KeyTheme.Grey ? null : list, theme: theme)));
        if (StockIcons.Parse(icon) is { } glyph)
            return ToImage(KeyRenderer.RenderOpenPng(list, glyph, theme: theme));
        using var image = IconExtractor.LoadCustomIcon(TargetResolver.Expand(icon));
        return ToImage(KeyRenderer.RenderOpenPng(list, image));
    }

    /// <summary>The key of a folder or link item: the list's icon (folder glyph by default), the link badge for a link.</summary>
    public static ImageSource RenderFolder(string name, string? icon, bool link, bool broken, KeyTheme theme)
    {
        if (broken)
            return ToImage(KeyRenderer.RenderOpenPng(name, Glyph.Question, link, theme));
        if (icon is null || StockIcons.Parse(icon) is { } _)
            return ToImage(KeyRenderer.RenderOpenPng(name, StockIcons.Parse(icon) ?? Glyph.Folder, link, theme));
        using var image = IconExtractor.LoadCustomIcon(TargetResolver.Expand(icon));
        return image is null
            ? ToImage(KeyRenderer.RenderOpenPng(name, Glyph.Folder, link, theme))
            : ToImage(KeyRenderer.RenderOpenPng(name, image, link));
    }

    public static ImageSource ToImage(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
