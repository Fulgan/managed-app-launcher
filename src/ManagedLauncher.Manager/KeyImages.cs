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
