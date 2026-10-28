namespace ManagedLauncher.Core.Icons;

public static class StockIcons
{
    public const string Prefix = "stock:";

    public static readonly IReadOnlyList<(string Name, Glyph Glyph)> All =
    [
        ("folder", Glyph.Folder), ("star", Glyph.Star), ("home", Glyph.Home),
        ("games", Glyph.Games), ("media", Glyph.Media), ("music", Glyph.Music),
        ("work", Glyph.Work), ("code", Glyph.Code), ("tools", Glyph.Tools),
        ("web", Glyph.Web), ("chat", Glyph.Chat), ("graphics", Glyph.Graphics),
    ];

    public static string Ref(string name) => Prefix + name;

    public static Glyph? Parse(string? icon)
    {
        if (icon is null || !icon.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var name = icon[Prefix.Length..].Trim();
        foreach (var stock in All)
        {
            if (string.Equals(stock.Name, name, StringComparison.OrdinalIgnoreCase))
                return stock.Glyph;
        }
        return null;
    }
}
