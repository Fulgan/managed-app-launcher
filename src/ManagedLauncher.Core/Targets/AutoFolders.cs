using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Core.Targets;

/// <summary>Where auto-folders read installed and running apps from; replaced in tests.</summary>
public interface IAppSources
{
    IReadOnlyList<SteamGame> SteamGames();

    /// <summary>Common and per-user Start menu Programs folders that exist.</summary>
    IReadOnlyList<string> StartMenuRoots();

    /// <summary>Store apps as (name, AUMID).</summary>
    IReadOnlyList<(string Name, string Aumid)> PackagedApps();

    IReadOnlyList<AppChoice> Running();
}

public sealed class SystemAppSources : IAppSources
{
    public IReadOnlyList<SteamGame> SteamGames() => SteamLibrary.Games();

    public IReadOnlyList<string> StartMenuRoots() =>
        new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs }
            .Select(Environment.GetFolderPath)
            .Where(Directory.Exists)
            .ToList();

    public IReadOnlyList<(string Name, string Aumid)> PackagedApps() =>
        Icons.ShellItems.EnumerateApps().Where(a => a.Aumid.Contains('!')).Select(a => (a.Name, a.Aumid)).ToList();

    public IReadOnlyList<AppChoice> Running() => AppCatalog.Running(Environment.ProcessId);
}

/// <summary>
/// Builds the items of auto-folders: lists whose content comes from a source (Steam, a directory, the Start menu,
/// running programs) each time they are shown, instead of being stored. Sub-directories become nested generated
/// folders with ids <c>listId/relative path</c>; hidden targets are left out and override entries win per target.
/// </summary>
public static class AutoFolders
{
    /// <summary>Separates an auto-folder's id from the relative path of a nested generated folder.</summary>
    public const char Separator = '/';

    public static string NestedId(string listId, string relativePath) => $"{listId}{Separator}{relativePath}";

    /// <summary>The auto-folder's id and the nested relative path ("" for the auto-folder itself).</summary>
    public static (string ListId, string Path) Split(string id)
    {
        var at = id.IndexOf(Separator);
        return at < 0 ? (id, "") : (id[..at], id[(at + 1)..]);
    }

    public static bool IsNested(string id) => id.Contains(Separator);

    /// <summary>
    /// The items of auto-folder <paramref name="listId"/> at <paramref name="path"/>: nested folders A–Z, then apps
    /// A–Z. <paramref name="folderLevels"/> = folder levels left below this one before the depth limit; 0 leaves
    /// nested folders out, and a sub-directory shows only when it holds a matching file within those levels.
    /// Unreadable sources yield what could be read; never throws for missing paths or a missing Steam.
    /// </summary>
    /// <param name="raw">Keep hidden items and ignore overrides, for editing them.</param>
    public static IReadOnlyList<Entry> Generate(StoredList list, string listId, string path, int folderLevels, IAppSources sources, bool raw = false)
    {
        if (list.Source is not { } source)
            return [];
        (List<(string Name, string Key)> Folders, List<Entry> Apps) generated = source.Type switch
        {
            SourceType.Steam => ([], sources.SteamGames().Select(g => g.ToEntry()).ToList()),
            SourceType.Running => ([], sources.Running().Select(c => Named(c.Entry, c.Name)).ToList()),
            SourceType.Directory => FromDirectory(source, path, folderLevels),
            SourceType.StartMenu => FromStartMenu(sources, path),
            _ => ([], []),
        };
        var (folders, apps) = generated;

        var hidden = new HashSet<string>(raw ? [] : list.Hidden ?? [], StringComparer.OrdinalIgnoreCase);
        var overrides = (raw ? [] : list.Items).Where(i => i.IsApp && !string.IsNullOrWhiteSpace(i.Target))
            .GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<Entry>();
        if (folderLevels > 0)
        {
            result.AddRange(folders
                .Where(f => !hidden.Contains(f.Key))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Entry { Folder = NestedId(listId, Combine(path, f.Name)), Title = f.Name, Target = f.Key }));
        }
        result.AddRange(apps
            .Where(a => !hidden.Contains(a.Target))
            .GroupBy(a => a.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => overrides.TryGetValue(g.Key, out var o) ? Merge(g.First(), o) : g.First())
            .OrderBy(a => a.Title ?? AppNames.Default(a), StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>The generated app with every field the override sets taking precedence.</summary>
    public static Entry Merge(Entry generated, Entry over)
    {
        var merged = new Entry
        {
            Target = generated.Target,
            Kind = over.Kind ?? generated.Kind,
            Title = over.Title ?? generated.Title,
            TrackPath = over.TrackPath ?? generated.TrackPath,
            Icon = over.Icon ?? generated.Icon,
        };
        merged.FillFrom(over);
        merged.FillFrom(generated);
        return merged;
    }

    /// <summary>The entry pinning this file would give; a shortcut that can't be read becomes a plain entry.</summary>
    private static Entry EntryFor(string path)
    {
        try
        {
            return PinAdvisor.Plan(path, null).Entry;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Entry { Target = path, Kind = TargetKindDetector.Detect(path) };
        }
    }

    /// <summary>
    /// The override that turns <paramref name="generated"/> into <paramref name="edited"/>: the target plus every
    /// field that differs; null when nothing differs.
    /// </summary>
    public static Entry? Diff(Entry edited, Entry generated)
    {
        static T? Changed<T>(T? value, T? original) => EqualityComparer<T>.Default.Equals(value, original) ? default : value;
        var diff = new Entry
        {
            Target = generated.Target,
            Title = Changed(edited.Title, generated.Title),
            Icon = Changed(edited.Icon, generated.Icon),
            TrackPath = Changed(edited.TrackPath, generated.TrackPath),
            Args = Changed(edited.Args, generated.Args),
            WorkDir = Changed(edited.WorkDir, generated.WorkDir),
            Admin = edited.Admin == generated.Admin ? null : edited.Admin,
            OnRunning = edited.OnRunning == generated.OnRunning ? null : edited.OnRunning,
            KillMode = edited.KillMode == generated.KillMode ? null : edited.KillMode,
            Indicator = edited.Indicator == generated.Indicator ? null : edited.Indicator,
            LongPress = edited.LongPress == generated.LongPress ? null : edited.LongPress,
        };
        var empty = diff.Title is null && diff.Icon is null && diff.TrackPath is null && diff.Args is null && diff.WorkDir is null
            && diff.Admin is null && diff.OnRunning is null && diff.KillMode is null && diff.Indicator is null && diff.LongPress is null;
        return empty ? null : diff;
    }

    /// <summary>What an auto-folder is built from, for people.</summary>
    public static string Describe(ListSource source) => source.Type switch
    {
        SourceType.Steam => "Steam games",
        SourceType.StartMenu => "Start menu",
        SourceType.Running => "running programs",
        _ => source.Path ?? "directory",
    };

    private static Entry Named(Entry entry, string name)
    {
        entry.Title ??= name;
        return entry;
    }

    private static string Combine(string path, string name) => path.Length == 0 ? name : $"{path}\\{name}";

    /// <summary>File types a directory auto-folder takes when it names none.</summary>
    public static readonly IReadOnlyList<string> AllTypes = ["exe", "lnk", "symlink", "bat"];

    private static (List<(string Name, string Key)>, List<Entry>) FromDirectory(ListSource source, string path, int folderLevels)
    {
        var folders = new List<(string, string)>();
        var apps = new List<Entry>();
        if (string.IsNullOrWhiteSpace(source.Path))
            return (folders, apps);
        var dir = Path.Combine(TargetResolver.Expand(source.Path), path);
        if (!Directory.Exists(dir))
            return (folders, apps);
        var types = new HashSet<string>(source.Types is { Count: > 0 } t ? t : AllTypes, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (folderLevels > 0)
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (HasFiles(sub, types, folderLevels - 1))
                        folders.Add((Path.GetFileName(sub), sub));
                }
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (TypeOf(file) is { } type && types.Contains(type))
                    apps.Add(EntryFor(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (folders, apps);
    }

    /// <summary>Whether the directory, or one of its sub-directories up to <paramref name="levels"/> deep, holds a file of the types.</summary>
    private static bool HasFiles(string dir, HashSet<string> types, int levels)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any(f => TypeOf(f) is { } type && types.Contains(type))
                || levels > 0 && Directory.EnumerateDirectories(dir).Any(sub => HasFiles(sub, types, levels - 1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"symlink" for a symbolic link file of any kind, else the type by extension; null for other files.</summary>
    internal static string? TypeOf(string file)
    {
        try
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                return "symlink";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".exe" => "exe",
            ".lnk" => "lnk",
            ".bat" or ".cmd" or ".ps1" => "bat",
            _ => null,
        };
    }

    /// <summary>
    /// Shortcuts of one Start menu folder, merged across the common and per-user roots; Store apps join the top
    /// level. A name seen twice in one folder is kept once.
    /// </summary>
    private static (List<(string Name, string Key)>, List<Entry>) FromStartMenu(IAppSources sources, string path)
    {
        var folders = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in sources.StartMenuRoots())
        {
            var dir = Path.Combine(root, path);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (HasShortcuts(sub))
                        folders.TryAdd(Path.GetFileName(sub), (Path.GetFileName(sub), "startmenu:" + Combine(path, Path.GetFileName(sub))));
                }
                foreach (var file in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        apps.TryAdd(name, Named(EntryFor(file), name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (path.Length == 0)
        {
            foreach (var (name, aumid) in sources.PackagedApps())
                apps.TryAdd(name, Named(EntryFor(aumid), name));
        }
        return (folders.Values.ToList(), apps.Values.ToList());
    }

    private static bool HasShortcuts(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
