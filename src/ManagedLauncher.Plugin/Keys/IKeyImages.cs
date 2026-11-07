using System.Collections.Concurrent;
using System.Drawing;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;

namespace ManagedLauncher.Plugin.Keys;

/// <summary>
/// Produces key images. Abstracted so key handling can be tested without the shell. Control and folder keys take the
/// tile colour of the list they are shown for.
/// </summary>
public interface IKeyImages
{
    Task<string> RenderAsync(KeyVisual visual, string? customIcon);

    /// <param name="page">Zero-based page shown.</param>
    string NextPage(int page, int pages, KeyTheme theme);

    /// <param name="page">Zero-based page shown.</param>
    string PreviousPage(int page, int pages, KeyTheme theme);

    string Blank { get; }

    string Back(KeyTheme theme);

    string Manage(KeyTheme theme);

    string Up(KeyTheme theme);

    /// <summary>
    /// Open Launcher key showing the list's <paramref name="icon"/> (null: the launcher glyph) above its name; the
    /// launcher glyph when it can't load.
    /// </summary>
    Task<string> OpenAsync(string list, string? icon, KeyTheme theme);

    /// <summary>Auto List key in a colour other than the grey of its shipped image.</summary>
    string AutoList(KeyTheme theme);

    /// <summary>
    /// Folder or link key: the list's <paramref name="icon"/> (null: the folder glyph) above its name, with the link
    /// badge for a link.
    /// </summary>
    Task<string> FolderAsync(string name, string? icon, bool link, KeyTheme theme);

    /// <summary>Link that can't open because it leads back to its own list.</summary>
    string BrokenLink(string name, KeyTheme theme);
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

    /// <summary>Rendered fixed images by name, theme and arguments.</summary>
    private readonly ConcurrentDictionary<string, string> rendered = new();

    private string Cached(string key, Func<byte[]> png) => rendered.GetOrAdd(key, _ => Convert.ToBase64String(png()));

    public string NextPage(int page, int pages, KeyTheme theme) =>
        Cached($"next|{page}|{pages}|{theme}", () => KeyRenderer.RenderNextPagePng(page, pages, theme));

    public string PreviousPage(int page, int pages, KeyTheme theme) =>
        Cached($"previous|{page}|{pages}|{theme}", () => KeyRenderer.RenderPreviousPagePng(page, pages, theme));

    private readonly Lazy<string> blank = new(() => Convert.ToBase64String(KeyRenderer.RenderBlankPng()));

    public string Blank => blank.Value;

    public string Back(KeyTheme theme) => Cached($"back|{theme}", () => KeyRenderer.RenderBackPng(theme));

    public string Manage(KeyTheme theme) => Cached($"manage|{theme}", () => KeyRenderer.RenderManagePng(theme));

    public string Up(KeyTheme theme) => Cached($"up|{theme}", () => KeyRenderer.RenderUpPng(theme));

    public string AutoList(KeyTheme theme) => Cached($"auto|{theme}", () => KeyRenderer.RenderAutoListPng(theme));

    public string BrokenLink(string name, KeyTheme theme) =>
        Cached($"broken|{name}|{theme}", () => KeyRenderer.RenderOpenPng(name, Glyph.Question, link: true, theme));

    public Task<string> FolderAsync(string name, string? icon, bool link, KeyTheme theme) =>
        TileOrImageAsync(name, icon, Glyph.Folder, link, theme);

    public Task<string> OpenAsync(string list, string? icon, KeyTheme theme) =>
        TileOrImageAsync(list, icon, Glyph.Launcher, link: false, theme);

    /// <summary>A stock glyph or <paramref name="fallback"/> on the themed tile, or a custom image above the name.</summary>
    private async Task<string> TileOrImageAsync(string name, string? icon, Glyph fallback, bool link, KeyTheme theme)
    {
        var glyph = icon is null ? fallback : StockIcons.Parse(icon);
        if (glyph is { } stock)
            return Cached($"tile|{name}|{stock}|{link}|{theme}", () => KeyRenderer.RenderOpenPng(name, stock, link, theme));
        var image = await LoadAsync(icon!, custom: true).ConfigureAwait(false);
        if (image is null)
            return Cached($"tile|{name}|{fallback}|{link}|{theme}", () => KeyRenderer.RenderOpenPng(name, fallback, link, theme));
        byte[] png;
        lock (render)
            png = KeyRenderer.RenderOpenPng(name, image, link);
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
