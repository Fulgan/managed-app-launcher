namespace ManagedLauncher.Core.Config;

public class AppConfig
{
    public const string DefaultListName = "default";

    public int LongPressMs { get; set; } = 2000;
    public int PollMs { get; set; } = 1500;
    public int CloseTimeoutMs { get; set; } = 2000;

    /// <summary>How long after a launch further presses only focus, so repeated presses during startup don't spawn copies.</summary>
    public int LaunchGraceMs { get; set; } = 5000;

    /// <summary>Shared settings groups of older configs; folded into their entries on load and never written back.</summary>
    [System.Text.Json.Serialization.JsonInclude, System.Text.Json.Serialization.JsonPropertyName("presets")]
    internal Dictionary<string, LaunchSettings>? LegacyPresets { get; set; }

    /// <summary>Named app lists in display order. The plugin pages through one list per key; lists never have gaps.</summary>
    public Dictionary<string, List<Entry>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decks dedicated to one list, by Stream Deck device id. The plugin keeps these decks on the launcher profile
    /// showing that list.
    /// </summary>
    public Dictionary<string, string> Decks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-list options by list name; lists without an entry use the defaults.</summary>
    public Dictionary<string, ListOptions> ListOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Old list name → new name, so keys whose List setting names a renamed list keep showing it.</summary>
    public Dictionary<string, string> Renames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Single-list form of older configs; <see cref="Normalize"/> moves it into the <c>default</c> list.</summary>
    public List<Entry>? Apps { get; set; }

    /// <summary>
    /// Guarantees the invariants the rest of the code relies on: the home list exists, legacy presets are folded in,
    /// collections are non-null, lists have no gaps, renames and decks only point at existing lists.
    /// </summary>
    public AppConfig Normalize()
    {

        var lists = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entries) in Lists ?? [])
            lists[lists.ContainsKey(name) ? UnusedName(lists, name) : name] = entries;
        Lists = lists;
        foreach (var name in Lists.Keys.ToList())
            Lists[name] = (Lists[name] ?? []).Where(IsValidItem).ToList();

        Renames = FirstWins(Renames, StringComparer.OrdinalIgnoreCase);
        foreach (var old in Renames.Keys.ToList())
        {
            if (Lists.ContainsKey(old) || !Lists.ContainsKey(ResolveList(old)))
                Renames.Remove(old);
        }

        Lists.TryAdd(HomeList, []);
        if (Apps is { Count: > 0 })
            Lists[HomeList].AddRange(Apps.Where(IsValidItem));
        Apps = null;
        FoldLegacyPresets();

        Decks = FirstWins((Decks ?? []).Where(d => d.Value is not null && Lists.ContainsKey(d.Value)), StringComparer.Ordinal);
        ListOptions = FirstWins((ListOptions ?? []).Where(o => o.Value is not null && Lists.ContainsKey(o.Key)), StringComparer.OrdinalIgnoreCase);
        return this;
    }

    private static bool IsValidItem(Entry? item) => item is not null && (item.IsBreak || !string.IsNullOrWhiteSpace(item.Target));

    /// <summary>Layout kinds of the named list's items, in order.</summary>
    public IReadOnlyList<Paging.ListItemKind> LayoutOf(string? list) => GetList(list).Select(e => e.LayoutKind).ToList();

    /// <summary>
    /// Older configs shared settings through named presets. Each entry takes its preset's values, then the
    /// <c>default</c> preset's, for every field it doesn't set itself, so it launches exactly as before.
    /// </summary>
    private void FoldLegacyPresets()
    {
        var presets = FirstWins(LegacyPresets?.Where(p => p.Value is not null), StringComparer.OrdinalIgnoreCase);
        var fallback = presets.GetValueOrDefault("default");
        foreach (var entry in Lists.Values.SelectMany(l => l))
        {
            if (entry.LegacyPreset is { } name && presets.TryGetValue(name, out var preset))
                entry.FillFrom(preset);
            entry.FillFrom(fallback);
            entry.LegacyPreset = null;
        }
        LegacyPresets = null;
    }

    /// <summary>
    /// Copies into a dictionary with <paramref name="comparer"/>. A hand-edited file can hold keys that differ only
    /// by case; the first one wins instead of the load failing.
    /// </summary>
    private static Dictionary<string, T> FirstWins<T>(IEnumerable<KeyValuePair<string, T>>? source, StringComparer comparer)
    {
        var result = new Dictionary<string, T>(comparer);
        foreach (var (key, value) in source ?? [])
            result.TryAdd(key, value);
        return result;
    }

    private static string UnusedName<T>(Dictionary<string, T> taken, string stem)
    {
        var name = stem;
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{stem} {n}";
        return name;
    }

    /// <summary>
    /// The list shown when nothing names one: the list called <c>default</c>, or what it was renamed to.
    /// It can't be removed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string HomeList => ResolveList(DefaultListName);

    /// <summary>The current name of a list, following renames; names that were never renamed come back unchanged.</summary>
    public string ResolveList(string? name)
    {
        name ??= DefaultListName;
        for (var hops = 0; hops <= Renames.Count && !Lists.ContainsKey(name) && Renames.TryGetValue(name, out var next); hops++)
            name = next;
        return name;
    }

    /// <summary><paramref name="stem"/>, or "stem 2", "stem 3"… whichever no list uses yet.</summary>
    public string UnusedListName(string stem) => UnusedName(Lists, stem);

    /// <summary>Why <paramref name="to"/> can't be the new name of list <paramref name="from"/>, or null when it can.</summary>
    public string? CheckListName(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0)
            return "A list needs a name.";
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && Lists.ContainsKey(to))
            return $"A list named '{to}' already exists.";
        return null;
    }

    /// <summary>
    /// Renames a list; decks and earlier renames follow it, and the old name keeps resolving to it.
    /// Returns why the rename was refused, or null when it was applied.
    /// </summary>
    public string? RenameList(string from, string to)
    {
        to = to.Trim();
        if (CheckListName(from, to) is { } problem)
            return problem;
        if (!Lists.TryGetValue(from, out var entries))
            return $"There is no list '{from}'.";
        if (from == to)
            return null;
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

        Lists.Remove(from);
        Lists[to] = entries;
        foreach (var old in Renames.Keys.ToList())
        {
            if (string.Equals(Renames[old], from, StringComparison.OrdinalIgnoreCase))
                Renames[old] = to;
        }
        Renames.Remove(to);
        if (!caseOnly)
            Renames[from] = to;
        foreach (var deck in Decks.Keys.ToList())
        {
            if (string.Equals(Decks[deck], from, StringComparison.OrdinalIgnoreCase))
                Decks[deck] = to;
        }
        if (ListOptions.Remove(from, out var options))
            ListOptions[to] = options;
        return null;
    }

    /// <summary>The named list, or an empty one when it doesn't exist. Use <see cref="EnsureList"/> to edit.</summary>
    public IReadOnlyList<Entry> GetList(string? name) =>
        Lists.TryGetValue(ResolveList(name), out var list) ? list : [];

    /// <summary>Creates an empty list under exactly this name; an old name of a renamed list stops pointing there.</summary>
    /// <returns>False when a list with that name already exists.</returns>
    public bool AddList(string name)
    {
        if (!Lists.TryAdd(name, []))
            return false;
        Renames.Remove(name);
        return true;
    }

    /// <summary>The list with this name or old name, created when neither exists.</summary>
    public List<Entry> EnsureList(string? name)
    {
        name = ResolveList(name);
        if (!Lists.TryGetValue(name, out var list))
            Lists[name] = list = [];
        return list;
    }

    /// <summary>
    /// Copies a list under a new name, entries included, so editing the copy leaves the source untouched.
    /// <paramref name="name"/> null or taken → "<c>source</c> copy", then "copy 2", "copy 3"… Returns the name used.
    /// </summary>
    public string CopyList(string source, string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is null || Lists.ContainsKey(name))
            name = UnusedListName($"{source} copy");
        var json = System.Text.Json.JsonSerializer.Serialize(GetList(source), ConfigStore.JsonOptions);
        Lists[name] = System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(json, ConfigStore.JsonOptions) ?? [];
        Renames.Remove(name);
        if (ListOptions.TryGetValue(ResolveList(source), out var options))
            ListOptions[name] = options.Clone();
        return name;
    }

    /// <summary>Changes the named list's options; options left at their defaults are removed.</summary>
    public void UpdateOptions(string list, Action<ListOptions> change)
    {
        list = ResolveList(list);
        var options = ListOptions.TryGetValue(list, out var current) ? current : new ListOptions();
        change(options);
        if (options.IsDefault)
            ListOptions.Remove(list);
        else
            ListOptions[list] = options;
    }

    /// <summary>Options of the named list (old names resolve); defaults when none are set.</summary>
    public ListOptions OptionsOf(string? list) =>
        ListOptions.TryGetValue(ResolveList(list), out var options) ? options : new ListOptions();

    /// <summary>The app at <paramref name="index"/>; null past the end or for a break.</summary>
    public Entry? GetApp(string? list, int index)
    {
        var apps = GetList(list);
        return index >= 0 && index < apps.Count && !apps[index].IsBreak ? apps[index] : null;
    }

    /// <returns>The index the entry ended up at.</returns>
    public int Insert(string? list, Entry entry, int? at = null)
    {
        var apps = EnsureList(list);
        var index = Math.Clamp(at ?? apps.Count, 0, apps.Count);
        apps.Insert(index, entry);
        return index;
    }

    public bool Remove(string? list, int index)
    {
        if (GetApp(list, index) is null)
            return false;
        Lists[ResolveList(list)].RemoveAt(index);
        return true;
    }

    public bool Move(string? list, int from, int to)
    {
        var apps = EnsureList(list);
        if (GetApp(list, from) is not { } entry || to < 0 || to >= apps.Count)
            return false;
        apps.RemoveAt(from);
        apps.Insert(to, entry);
        return true;
    }
}
