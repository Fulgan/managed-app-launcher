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

    /// <summary>Open Launcher key showing the list's <paramref name="icon"/> above its name; the launcher glyph when it can't load.</summary>
    Task<string> OpenAsync(string list, string icon);

}

public sealed class ShellKeyImages(StaWorker sta) : IKeyImages
{
    /// <summary>
    /// One load per icon file version: concurrent requests share it, and a changed file (new write time) gets a new
    /// entry, so config reloads need not drop the cache.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GDI+ bitmaps can't be drawn from two threads at once.</summary>
    private readonly Lock render = new();
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

    public async Task<string> OpenAsync(string list, string icon)
    {
        if (StockIcons.Parse(icon) is { } glyph)
            return opens.GetOrAdd($"{list}|{glyph}", _ => Convert.ToBase64String(KeyRenderer.RenderOpenPng(list, glyph)));
        var image = await LoadAsync(icon, custom: true).ConfigureAwait(false);
        if (image is null)
            return opens.GetOrAdd(list, l => Convert.ToBase64String(KeyRenderer.RenderOpenPng(l)));
        byte[] png;
        lock (render)
            png = KeyRenderer.RenderOpenPng(list, image);
        return Convert.ToBase64String(png);
    }

    public async Task<string> RenderAsync(KeyVisual visual, string? customIcon)
    {
        var source = customIcon ?? visual.IconSource;
        var icon = source is null ? null : await LoadAsync(source, custom: customIcon is not null).ConfigureAwait(false);

        byte[] png;
        lock (render)
            png = KeyRenderer.RenderPng(icon, visual);
        return Convert.ToBase64String(png);
    }

    private Task<Bitmap?> LoadAsync(string source, bool custom) =>
        icons.GetOrAdd(CacheKey(source), _ => new Lazy<Task<Bitmap?>>(() => sta.RunAsync(() => custom
            ? IconExtractor.LoadCustomIcon(source)
            : IconExtractor.ExtractBest(source)))).Value;

    private static string CacheKey(string source)
    {
        try
        {
            return File.Exists(source) ? $"{source}|{File.GetLastWriteTimeUtc(source).Ticks}" : source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return source;
        }
    }
}
