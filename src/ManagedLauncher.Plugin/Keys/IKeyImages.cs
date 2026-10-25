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

    public string Open(string list) => opens.GetOrAdd(list, l => Convert.ToBase64String(KeyRenderer.RenderOpenPng(l)));

    public async Task<string> RenderAsync(KeyVisual visual, string? customIcon)
    {
        var source = customIcon ?? visual.IconSource;
        Bitmap? icon = null;
        if (source is not null)
        {
            var load = icons.GetOrAdd(CacheKey(source), _ => new Lazy<Task<Bitmap?>>(() => sta.RunAsync(() => customIcon is not null
                ? IconExtractor.LoadCustomIcon(customIcon)
                : IconExtractor.ExtractBest(source))));
            icon = await load.Value.ConfigureAwait(false);
        }

        byte[] png;
        lock (render)
            png = KeyRenderer.RenderPng(icon, visual);
        return Convert.ToBase64String(png);
    }

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
