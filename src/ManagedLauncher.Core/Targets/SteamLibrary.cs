using System.Text.RegularExpressions;
using ManagedLauncher.Core.Config;
using Microsoft.Win32;

namespace ManagedLauncher.Core.Targets;

/// <param name="InstallDir">Game folder; any process running from it counts as the game.</param>
/// <param name="Icon">Icon Steam caches for the game, or null.</param>
public sealed record SteamGame(int AppId, string Name, string InstallDir, string? Icon)
{
    /// <summary>
    /// An entry that starts the game through Steam (so Steam handles updates, DRM and launch options) and tracks it
    /// by its install folder, since the game's exe isn't known up front.
    /// </summary>
    public Entry ToEntry() => new()
    {
        Target = $"steam://rungameid/{AppId}",
        Kind = TargetKind.Url,
        Title = Name,
        TrackPath = InstallDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        Icon = Icon,
    };
}

/// <summary>Installed Steam games, read from Steam's own library files; Steam doesn't need to run.</summary>
public static partial class SteamLibrary
{
    /// <summary>Steam's runtimes, redistributables and tools that install like games but aren't launched by people.</summary>
    private static readonly HashSet<int> NotGames = [228980, 1070560, 1391110, 1628350, 1493710];

    public static string? InstallPath()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public static IReadOnlyList<SteamGame> Games() => InstallPath() is { } steam ? Games(steam) : [];

    /// <summary>Every library listed in <c>libraryfolders.vdf</c> (plus Steam's own folder), every app manifest in each.</summary>
    public static IReadOnlyList<SteamGame> Games(string steam)
    {
        var libraries = new List<string> { steam };
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
            libraries.AddRange(Values(ReadText(folders), "path"));

        var games = new Dictionary<int, SteamGame>();
        foreach (var library in libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
                continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = ReadText(manifest);
                if (!int.TryParse(Values(text, "appid").FirstOrDefault(), out var id) || NotGames.Contains(id) || games.ContainsKey(id))
                    continue;
                var name = Values(text, "name").FirstOrDefault();
                var dir = Values(text, "installdir").FirstOrDefault();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dir) || name.StartsWith("Proton ", StringComparison.Ordinal)
                    || name.StartsWith("Steam Linux Runtime", StringComparison.Ordinal))
                    continue;
                games[id] = new SteamGame(id, name, Path.Combine(apps, "common", dir), CachedIcon(steam, id));
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The game icon Steam caches: a hash-named jpg in the app's cache folder, or the older <c>{id}_icon.jpg</c>.</summary>
    private static string? CachedIcon(string steam, int id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var folder = Path.Combine(cache, id.ToString());
        if (Directory.Exists(folder))
        {
            var hashed = Directory.EnumerateFiles(folder, "*.jpg").FirstOrDefault(f => IconName().IsMatch(Path.GetFileNameWithoutExtension(f)));
            if (hashed is not null)
                return hashed;
        }
        var flat = Path.Combine(cache, $"{id}_icon.jpg");
        return File.Exists(flat) ? flat : null;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Values of every <c>"key" "value"</c> pair with that key in a Valve KeyValues text file.</summary>
    private static IEnumerable<string> Values(string text, string key) =>
        Pair().Matches(text)
            .Where(m => string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["value"].Value.Replace(@"\\", @"\"));

    [GeneratedRegex("\"(?<key>[^\"\\\\]+)\"[ \\t]+\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Pair();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex IconName();
}
