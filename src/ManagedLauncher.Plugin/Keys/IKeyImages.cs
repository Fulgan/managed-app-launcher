using System.Collections.Concurrent;
using System.Drawing;
using ManagedLauncher.Core.Icons;

namespace ManagedLauncher.Plugin.Keys;

/// <summary>Produces key images. Abstracted so key handling can be tested without the shell.</summary>
public interface IKeyImages
{
    Task<string> RenderAsync(KeyVisual visual, string? customIcon);

    /// <param name="page">Zero-based page shown.</param>
    string NextPage(int page, int pages);

    string Blank { get; }

    string Back { get; }

    string Manage { get; }

    /// <summary>Open Launcher key labelled with <paramref name="list"/>.</summary>
    string Open(string list);

    void Invalidate();
}

public sealed class ShellKeyImages(StaWorker sta) : IKeyImages
{
    private readonly ConcurrentDictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(int, int), string> nextPages = new();

    public string NextPage(int page, int pages) =>
        nextPages.GetOrAdd((page, pages), k => Convert.ToBase64String(KeyRenderer.RenderNextPagePng(k.Item1, k.Item2)));

    private readonly Lazy<string> blank = new(() => Convert.ToBase64String(KeyRenderer.RenderBlankPng()));

    public string Blank => blank.Value;

    private readonly Lazy<string> back = new(() => Convert.ToBase64String(KeyRenderer.RenderBackPng()));

    public string Back => back.Value;

    private readonly Lazy<string> manage = new(() => Convert.ToBase64String(KeyRenderer.RenderManagePng()));

    public string Manage => manage.Value;

    private readonly ConcurrentDictionary<string, string> opens = new();

    public string Open(string list) => opens.GetOrAdd(list, l => Convert.ToBase64String(KeyRenderer.RenderOpenPng(l)));

    public async Task<string> RenderAsync(KeyVisual visual, string? customIcon)
    {
        var source = customIcon ?? visual.IconSource;
        Bitmap? icon = null;
        if (source is not null && !icons.TryGetValue(source, out icon))
        {
            icon = await sta.RunAsync(() => customIcon is not null
                ? IconExtractor.LoadImageFile(customIcon)
                : IconExtractor.ExtractBest(source)).ConfigureAwait(false);
            icons[source] = icon;
        }

        byte[] png;
        lock (icons)
        {
            if (source is not null && !icons.TryGetValue(source, out icon))
                icon = null;
            png = KeyRenderer.RenderPng(icon, visual);
        }
        return Convert.ToBase64String(png);
    }

    public void Invalidate()
    {
        lock (icons)
        {
            foreach (var icon in icons.Values)
                icon?.Dispose();
            icons.Clear();
        }
    }
}
