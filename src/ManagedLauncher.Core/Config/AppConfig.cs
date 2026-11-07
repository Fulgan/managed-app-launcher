using System.Text.Json;
using System.Text.Json.Serialization;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Config;

public class AppConfig
{
    /// <summary>Id of the home list, the list shown when nothing names one. It can be renamed but not removed.</summary>
    public const string DefaultListName = "default";

    /// <summary>Folder levels allowed when no list on the path sets <see cref="ListOptions.MaxDepth"/>.</summary>
    public const int DefaultMaxDepth = 4;

    public int LongPressMs { get; set; } = 2000;
    public int PollMs { get; set; } = 1500;
    public int CloseTimeoutMs { get; set; } = 2000;

    /// <summary>How long after a launch further presses only focus, so repeated presses during startup don't spawn copies.</summary>
    public int LaunchGraceMs { get; set; } = 5000;

    /// <summary>Shared settings groups of older configs; folded into their entries on load and never written back.</summary>
    [JsonInclude, JsonPropertyName("presets")]
    internal Dictionary<string, LaunchSettings>? LegacyPresets { get; set; }

    /// <summary>
    /// Every list by id: top-level lists and folder contents. Each list is either in <see cref="Top"/> or owned by
    /// exactly one folder item, so the lists form a tree; link items point anywhere without owning.
    /// </summary>
    public Dictionary<string, StoredList> Store { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Ids of the top-level lists in display order; the home list is always one of them.</summary>
    [JsonIgnore]
    public List<string> Top { get; set; } = [];

    /// <summary>
    /// <c>lists</c> in the file: top-level list name → list id. Older configs hold name → items there instead;
    /// <see cref="Normalize"/> migrates those.
    /// </summary>
    [JsonInclude, JsonPropertyName("lists")]
    internal Dictionary<string, JsonElement>? ListsJson
    {
        get => Top.Where(Store.ContainsKey).DistinctBy(id => Store[id].Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => Store[id].Name, id => JsonSerializer.SerializeToElement(id));
        set => rawLists = value;
    }

    private Dictionary<string, JsonElement>? rawLists;

    /// <summary>
    /// Decks dedicated to one list, by Stream Deck device id → list id. The plugin keeps these decks on the launcher
    /// profile showing that list.
    /// </summary>
    public Dictionary<string, string> Decks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Former top-level list names → list id, so keys still carrying a name find their list.</summary>
    public Dictionary<string, string> Renames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Named lists of older configs; moved into <see cref="Store"/> on load and never written back.</summary>
    private Dictionary<string, List<Entry>>? LegacyLists { get; set; }

    /// <summary>Per-list options of older configs by list name; moved into the stored lists on load.</summary>
    [JsonInclude, JsonPropertyName("listOptions")]
    internal Dictionary<string, ListOptions>? LegacyListOptions { get; set; }

    /// <summary>Single-list form of older configs; <see cref="Normalize"/> moves it into the home list.</summary>
    public List<Entry>? Apps { get; set; }

    [JsonIgnore]
    public string HomeList => DefaultListName;

    /// <summary>
    /// Guarantees the invariants the rest of the code relies on: older layouts are migrated, the home list exists and
    /// is top-level, every list has one owner, items only point at existing lists, links that loop back are flagged,
    /// decks and renames only point at existing lists, legacy presets are folded in.
    /// </summary>
    public AppConfig Normalize()
    {
        Store = new Dictionary<string, StoredList>(
            (Store ?? []).Where(s => s.Value is not null).Select(s => KeyValuePair.Create(s.Key.ToLowerInvariant(), s.Value)).DistinctBy(s => s.Key),
            StringComparer.Ordinal);
        Renames = FirstWins(Renames, StringComparer.OrdinalIgnoreCase);
        Decks = FirstWins(Decks, StringComparer.Ordinal);
        ReadLists();
        if (Store.Count == 0 && LegacyLists is not null)
            MigrateLegacyLists();
        LegacyLists = null;
        LegacyListOptions = null;

        if (!Store.TryGetValue(HomeList, out var home))
            Store[HomeList] = home = new StoredList { Name = DefaultListName };
        if (Apps is { Count: > 0 })
            home.Items.AddRange(Apps);
        Apps = null;

        foreach (var list in Store.Values)
        {
            list.Name = string.IsNullOrWhiteSpace(list.Name) ? "List" : list.Name.Trim();
            list.Items = (list.Items ?? []).Where(IsValidItem).Where(i => list.Kind == ListKind.Static || i.IsApp).ToList();
            if (list.Kind == ListKind.Dynamic && list.Source is null)
                list.Kind = ListKind.Static;
            if (list.Kind == ListKind.Static)
                list.Hidden = null;
            else if (list.Hidden is { Count: 0 })
                list.Hidden = null;
            foreach (var item in list.Items)
            {
                item.Folder = item.Folder?.ToLowerInvariant();
                item.Link = item.Link?.ToLowerInvariant();
            }
            if (list.Options is { IsDefault: true })
                list.Options = null;
        }
        FoldLegacyPresets();
        BuildTree();
        foreach (var (id, list) in Store)
            list.IsTopLevel = Top.Contains(id);
        FlagLoopingLinks();

        foreach (var name in Renames.Keys.ToList())
        {
            if (!Store.ContainsKey(Renames[name]) || TopByName(name) is not null)
                Renames.Remove(name);
        }
        foreach (var deck in Decks.Keys.ToList())
        {
            var id = Decks[deck] is { } value ? ResolveList(value) : null;
            if (id is null || !Store.ContainsKey(id))
                Decks.Remove(deck);
            else
                Decks[deck] = id;
        }
        return this;
    }

    /// <summary>
    /// Splits the file's <c>lists</c>: a name → id pair is a top-level list (its name comes from the key), a name →
    /// items pair is a list of an older config. A hand-edited name used twice (differing by case) keeps the first.
    /// </summary>
    private void ReadLists()
    {
        if (rawLists is null)
            return;
        var top = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in rawLists)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString()?.ToLowerInvariant() is { } id && Store.TryGetValue(id, out var list))
            {
                if (!names.Add(name))
                    continue;
                list.Name = name;
                top.Add(id);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                LegacyLists ??= [];
                LegacyLists[name] = value.Deserialize<List<Entry>>(ConfigStore.JsonOptions) ?? [];
            }
        }
        if (top.Count > 0)
            Top = top;
        rawLists = null;
    }

    private bool IsValidItem(Entry? item) =>
        item is not null && (item.IsBreak || item.Opens is { } id && Store.ContainsKey(id.ToLowerInvariant()) || item.IsApp && !string.IsNullOrWhiteSpace(item.Target));

    /// <summary>
    /// Each legacy list becomes a top-level stored list; the one <c>default</c> resolves to through the old rename
    /// chain becomes the home list. Old names keep resolving through <see cref="Renames"/>.
    /// </summary>
    private void MigrateLegacyLists()
    {
        var oldRenames = Renames;
        string Chain(string name)
        {
            for (var hops = 0; hops <= oldRenames.Count && !LegacyLists!.ContainsKey(name) && oldRenames.TryGetValue(name, out var next); hops++)
                name = next;
            return name;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var homeName = Chain(DefaultListName);
        foreach (var (rawName, entries) in LegacyLists!)
        {
            var name = names.ContainsKey(rawName) ? UnusedName(names, rawName) : rawName;
            var id = string.Equals(rawName, homeName, StringComparison.OrdinalIgnoreCase) && !Store.ContainsKey(HomeList) ? HomeList : MigratedId(name);
            names[name] = id;
            Store[id] = new StoredList { Name = name, Items = entries ?? [] };
            Top.Add(id);
        }
        foreach (var (name, options) in LegacyListOptions ?? [])
        {
            if (options is not null && names.TryGetValue(name, out var id) && Store[id].Options is null)
                Store[id].Options = options;
        }

        Renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in oldRenames.Keys)
        {
            if (names.TryGetValue(Chain(old), out var id))
                Renames[old] = id;
        }
        foreach (var deck in Decks.Keys.ToList())
        {
            if (Decks[deck] is { } name && names.TryGetValue(Chain(name), out var id))
                Decks[deck] = id;
        }
    }

    /// <summary>
    /// Makes the lists a tree: top-level ids are kept once each, the home list is top-level, a folder item pointing
    /// at a list that already has an owner is dropped, and a list nobody owns becomes top-level.
    /// </summary>
    private void BuildTree()
    {
        Top = (Top ?? []).Where(id => id is not null).Select(id => id.ToLowerInvariant()).Where(Store.ContainsKey).Distinct().ToList();
        if (!Top.Contains(HomeList))
            Top.Insert(0, HomeList);

        var owned = new HashSet<string>(Top, StringComparer.Ordinal);
        void Visit(string id)
        {
            var items = Store[id].Items;
            items.RemoveAll(item => item.IsFolder && !owned.Add(item.Folder!));
            foreach (var child in items.Where(i => i.IsFolder).Select(i => i.Folder!).ToList())
                Visit(child);
        }

        foreach (var id in Top.ToList())
            Visit(id);
        foreach (var id in Store.Keys.ToList())
        {
            if (owned.Add(id))
            {
                Top.Add(id);
                Visit(id);
            }
        }
    }

    /// <summary>Checks every link in tree order; a link already flagged no longer counts for the ones after it.</summary>
    private void FlagLoopingLinks()
    {
        var links = TreeOrder().SelectMany(id => Store[id].Items.Where(i => i.IsLink).Select(item => (id, item))).ToList();
        foreach (var (_, item) in links)
            item.LinkProblem = null;
        foreach (var (id, item) in links)
            item.LinkProblem = CheckLink(id, item.Link!);
    }

    private void FoldLegacyPresets()
    {
        var presets = FirstWins(LegacyPresets?.Where(p => p.Value is not null), StringComparer.OrdinalIgnoreCase);
        var fallback = presets.GetValueOrDefault("default");
        foreach (var entry in Store.Values.SelectMany(l => l.Items))
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
    /// Id of a migrated list, derived from its name: every program that loads the same old file before it is saved
    /// in the new layout gets the same ids, so keys the plugin points at an id still find their list after the
    /// manager or CLI saves.
    /// </summary>
    private string MigratedId(string name)
    {
        for (var n = 0; ; n++)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{name.ToLowerInvariant()}|{n}"));
            var id = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            if (!Store.ContainsKey(id) && id != HomeList)
                return id;
        }
    }

    private string NewId()
    {
        string id;
        do
            id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        while (Store.ContainsKey(id) || id == HomeList);
        return id;
    }

    // ---- lookup ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The id a list reference stands for: a list id, a top-level name, a path of names from the top joined by
    /// <c>/</c>, or a former top-level name. Null means the home list. An unknown reference comes back unchanged.
    /// </summary>
    public string ResolveList(string? reference)
    {
        if (reference is null)
            return HomeList;
        if (Store.ContainsKey(reference) || Store.ContainsKey(reference.ToLowerInvariant()) && IsId(reference))
            return reference.ToLowerInvariant();
        if (TopByName(reference) is { } top)
            return top;
        if (reference.Contains('/') && ByPath(reference) is { } nested)
            return nested;
        if (Renames.TryGetValue(reference, out var renamed) && Store.ContainsKey(renamed))
            return renamed;
        return reference;
    }

    private static bool IsId(string reference) => reference.Length == 8 && reference.All(char.IsAsciiHexDigit);

    private string? TopByName(string name) =>
        Top.FirstOrDefault(id => Store.TryGetValue(id, out var list) && string.Equals(list.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private string? ByPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || TopByName(parts[0]) is not { } id)
            return null;
        foreach (var part in parts.Skip(1))
        {
            id = Children(id).FirstOrDefault(c => string.Equals(Store[c].Name, part, StringComparison.OrdinalIgnoreCase))!;
            if (id is null)
                return null;
        }
        return id;
    }

    /// <summary>Whether the reference names a stored list or a folder generated inside an auto-folder.</summary>
    public bool Exists(string? reference)
    {
        var id = ResolveList(reference);
        return Store.ContainsKey(id) || AutoFolders.IsNested(id) && IsDynamic(AutoFolders.Split(id).ListId);
    }

    /// <summary>Whether the list is an auto-folder, or a folder generated inside one.</summary>
    public bool IsDynamic(string? reference) =>
        Store.TryGetValue(AutoFolders.Split(ResolveList(reference)).ListId, out var list) && list.Kind == ListKind.Dynamic;

    /// <summary>The stored list behind a reference: itself, or the auto-folder a generated folder belongs to.</summary>
    private string StoredIdOf(string? reference) => AutoFolders.Split(ResolveList(reference)).ListId;

    /// <summary>The list's name; the reference itself when no such list exists.</summary>
    public string NameOf(string? reference)
    {
        var id = ResolveList(reference);
        if (Store.TryGetValue(id, out var list))
            return list.Name;
        var (_, path) = AutoFolders.Split(id);
        return path.Length > 0 ? path[(path.LastIndexOf('\\') + 1)..] : reference ?? DefaultListName;
    }

    /// <summary>Names from the top down to the list, joined by <c>/</c>.</summary>
    public string PathOf(string? reference)
    {
        var id = ResolveList(reference);
        var (stored, nested) = AutoFolders.Split(id);
        var path = string.Join("/", AncestorsOf(stored).Reverse().Select(a => Store[a].Name));
        return nested.Length == 0 ? path : $"{path}/{nested.Replace('\\', '/')}";
    }

    /// <summary>
    /// The items an auto-folder or a folder generated in it shows right now; folders generated below the depth limit
    /// are left out. Empty for static lists.
    /// </summary>
    /// <param name="raw">Keep hidden items and ignore overrides, for editing them.</param>
    public IReadOnlyList<Entry> Generate(string? reference, IAppSources sources, bool raw = false)
    {
        var id = ResolveList(reference);
        var (stored, nested) = AutoFolders.Split(id);
        if (!Store.TryGetValue(stored, out var list) || list.Kind != ListKind.Dynamic)
            return [];
        return AutoFolders.Generate(list, stored, nested, Math.Max(0, LimitOf(stored) - DepthOf(id)), sources, raw);
    }

    /// <summary>
    /// Stores how a generated app of an auto-folder was edited: the fields that differ from what the source gives,
    /// replacing any earlier override for the same target; no difference removes the override.
    /// </summary>
    public void SetOverride(string? reference, Entry edited, Entry generated)
    {
        if (!Store.TryGetValue(StoredIdOf(reference), out var list) || list.Kind != ListKind.Dynamic)
            return;
        list.Items.RemoveAll(i => string.Equals(i.Target, generated.Target, StringComparison.OrdinalIgnoreCase));
        if (AutoFolders.Diff(edited, generated) is { } diff)
            list.Items.Add(diff);
    }

    /// <summary>Hides or shows a generated item (by target or directory) of an auto-folder.</summary>
    public void SetHidden(string? reference, string target, bool hidden)
    {
        if (!Store.TryGetValue(StoredIdOf(reference), out var list) || list.Kind != ListKind.Dynamic)
            return;
        var set = list.Hidden ??= [];
        set.RemoveAll(h => string.Equals(h, target, StringComparison.OrdinalIgnoreCase));
        if (hidden)
            set.Add(target);
    }

    /// <summary>Whether a generated item of an auto-folder is hidden.</summary>
    public bool IsHidden(string? reference, string target) =>
        Store.TryGetValue(StoredIdOf(reference), out var list) && (list.Hidden ?? []).Contains(target, StringComparer.OrdinalIgnoreCase);

    /// <summary>The auto-folder's source; null for static lists.</summary>
    public ListSource? SourceOf(string? reference) =>
        Store.TryGetValue(StoredIdOf(reference), out var list) && list.Kind == ListKind.Dynamic ? list.Source : null;

    /// <summary>Why items can't be added to, moved into or out of an auto-folder.</summary>
    public const string FillsItself = "Auto folders fill themselves; items can't be added or moved there.";

    /// <summary>Adds an auto-folder to a list. Returns why it was refused, or null.</summary>
    public string? AddAutoFolder(string? parent, string name, ListSource source, out string id, out int index, int? at = null)
    {
        if (AddFolder(parent, name, out id, out index, at) is { } problem)
            return problem;
        Store[id].Kind = ListKind.Dynamic;
        Store[id].Source = source;
        return null;
    }

    /// <summary>The text a folder or link item shows: the link's own title, else the list's name.</summary>
    public string TitleOf(Entry item) =>
        item.IsLink && !string.IsNullOrWhiteSpace(item.Title) ? item.Title! : item.Opens is { } id ? NameOf(id) : item.Title ?? "";

    /// <summary>Ids of the folders inside a list, in item order.</summary>
    public IReadOnlyList<string> Children(string? reference) =>
        GetList(reference).Where(i => i.IsFolder).Select(i => i.Folder!).Where(Store.ContainsKey).ToList();

    /// <summary>The list whose folder item owns this list; null for top-level and unknown lists.</summary>
    public string? ParentOf(string? reference)
    {
        var id = ResolveList(reference);
        return Store.FirstOrDefault(s => s.Value.Items.Any(i => i.IsFolder && i.Folder == id)).Key;
    }

    /// <summary>The list itself, its parent, and so on up to its top-level list.</summary>
    public IEnumerable<string> AncestorsOf(string? reference)
    {
        var id = ResolveList(reference);
        if (!Store.ContainsKey(id))
            yield break;
        var seen = new HashSet<string>();
        for (string? current = id; current is not null && seen.Add(current); current = ParentOf(current))
            yield return current;
    }

    /// <summary>Folder level of the list: 1 for top-level lists; a generated folder counts its nesting in the auto-folder.</summary>
    public int DepthOf(string? reference)
    {
        var (stored, nested) = AutoFolders.Split(ResolveList(reference));
        return Math.Max(1, AncestorsOf(stored).Count()) + (nested.Length == 0 ? 0 : nested.Count(c => c == '\\') + 1);
    }

    /// <summary>Deepest level allowed for the list: the nearest <see cref="ListOptions.MaxDepth"/> on its path, else the default.</summary>
    public int LimitOf(string? reference) => LimitAlong(AncestorsOf(StoredIdOf(reference)));

    private int LimitAlong(IEnumerable<string> ancestors) =>
        ancestors.Select(a => Store[a].Options?.MaxDepth).FirstOrDefault(d => d is not null) ?? DefaultMaxDepth;

    /// <summary>Page keys of the list: its own setting, else the nearest list above it that sets one, else carousel.</summary>
    public Navigation NavigationOf(string? reference) =>
        AncestorsOf(StoredIdOf(reference)).Select(a => Store[a].Options?.Navigation).FirstOrDefault(n => n is not null) ?? Navigation.Carousel;

    /// <summary>The style an unset list would take from the lists above it.</summary>
    public Navigation InheritedNavigationOf(string? reference) =>
        AncestorsOf(reference).Skip(1).Select(a => Store[a].Options?.Navigation).FirstOrDefault(n => n is not null) ?? Navigation.Carousel;

    /// <summary>Whether Back or Up repeats on every page: the list's own setting, else the nearest list above it that sets one, else first page only.</summary>
    public bool BackOnEveryPageOf(string? reference) =>
        AncestorsOf(StoredIdOf(reference)).Select(a => Store[a].Options?.BackOnEveryPage).FirstOrDefault(b => b is not null) ?? false;

    /// <summary>The Back setting an unset list would take from the lists above it.</summary>
    public bool InheritedBackOnEveryPageOf(string? reference) =>
        AncestorsOf(StoredIdOf(reference)).Skip(1).Select(a => Store[a].Options?.BackOnEveryPage).FirstOrDefault(b => b is not null) ?? false;

    /// <summary>Tile colour of the keys shown for the list: its own, else the nearest list above it that sets one, else grey.</summary>
    public KeyTheme ThemeOf(string? reference) =>
        AncestorsOf(StoredIdOf(reference)).Select(a => Store[a].Options?.Theme).FirstOrDefault(t => t is not null) ?? KeyTheme.Grey;

    /// <summary>The tile colour an unset list would take from the lists above it.</summary>
    public KeyTheme InheritedThemeOf(string? reference) =>
        AncestorsOf(StoredIdOf(reference)).Skip(1).Select(a => Store[a].Options?.Theme).FirstOrDefault(t => t is not null) ?? KeyTheme.Grey;

    /// <summary>The list and every list its folders own, depth first.</summary>
    public IEnumerable<string> SubtreeOf(string? reference)
    {
        var id = ResolveList(reference);
        if (!Store.ContainsKey(id))
            yield break;
        var seen = new HashSet<string>();
        var stack = new Stack<string>([id]);
        while (stack.TryPop(out var current))
        {
            if (!seen.Add(current))
                continue;
            yield return current;
            foreach (var child in Children(current).Reverse())
                stack.Push(child);
        }
    }

    /// <summary>Every list in display order: each top-level list followed by its folders, depth first, within depth limits.</summary>
    public IReadOnlyList<(string Id, int Depth)> Tree()
    {
        var result = new List<(string, int)>();
        var seen = new HashSet<string>();
        void Visit(string id, int depth, int limit)
        {
            if (!seen.Add(id))
                return;
            result.Add((id, depth));
            limit = Store[id].Options?.MaxDepth ?? limit;
            if (depth >= limit)
                return;
            foreach (var child in Children(id))
                Visit(child, depth + 1, limit);
        }

        foreach (var id in Top.Where(Store.ContainsKey))
            Visit(id, 1, DefaultMaxDepth);
        return result;
    }

    private IEnumerable<string> TreeOrder() => Tree().Select(t => t.Id);

    /// <summary>Apps and folders inside the list and all of its folders.</summary>
    public (int Apps, int Folders) ContentsOf(string? reference)
    {
        var lists = SubtreeOf(reference).ToList();
        return (lists.Sum(id => Store[id].Items.Count(i => i.IsApp)), lists.Count - 1);
    }

    // ---- structure checks --------------------------------------------------------------------------------------

    /// <summary>
    /// Why a link in <paramref name="list"/> can't open <paramref name="target"/>, or null when it can. Refused when
    /// the target is the list or one of its ancestors, when nothing below the list's depth limit is left, or when
    /// following folders and working links from the target, within that remaining depth, leads back to the list or
    /// an ancestor.
    /// </summary>
    public string? CheckLink(string? list, string? target)
    {
        var from = ResolveList(list);
        var to = ResolveList(target);
        if (!Store.ContainsKey(from) || !Store.ContainsKey(to))
            return "That list doesn't exist.";
        var ancestors = AncestorsOf(from).ToHashSet();
        if (to == from)
            return "A link can't open the list it is in.";
        if (ancestors.Contains(to))
            return $"'{Store[to].Name}' already contains this list.";
        var budget = LimitOf(from) - DepthOf(from);
        if (budget < 1)
            return $"Links can't go deeper than {LimitOf(from)} levels here.";

        var seen = new HashSet<string> { to };
        var level = new List<string> { to };
        for (var depth = 1; depth <= budget && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var id in level)
            {
                foreach (var item in Store[id].Items)
                {
                    var opens = item.IsFolder ? item.Folder : item.IsLink && item.LinkProblem is null ? item.Link : null;
                    if (opens is null || !Store.ContainsKey(opens))
                        continue;
                    if (ancestors.Contains(opens))
                        return $"'{Store[to].Name}' leads back to '{Store[opens].Name}'.";
                    if (seen.Add(opens))
                        next.Add(opens);
                }
            }
            level = next;
        }
        return null;
    }

    /// <summary>Why the folder list <paramref name="folder"/> can't move into <paramref name="parent"/>, or null when it can.</summary>
    public string? CheckFolderPlacement(string? parent, string? folder)
    {
        var to = ResolveList(parent);
        var id = ResolveList(folder);
        if (!Store.ContainsKey(to) || !Store.ContainsKey(id))
            return "That list doesn't exist.";
        if (id == HomeList)
            return "The home list can't go into a folder.";
        if (AncestorsOf(to).Contains(id))
            return "A folder can't go inside itself.";
        return CheckDepth(to, id);
    }

    /// <summary>Why a copy of folder list <paramref name="id"/> can't go into <paramref name="to"/> without breaking a depth limit, or null.</summary>
    private string? CheckDepth(string to, string id)
    {
        var parentPath = AncestorsOf(to).ToList();
        IEnumerable<string> PathInside(string member)
        {
            var path = new List<string>();
            for (string? current = member; current is not null && current != id; current = ParentOf(current))
                path.Add(current);
            path.Add(id);
            return path;
        }

        foreach (var member in SubtreeOf(id))
        {
            var path = PathInside(member).Concat(parentPath).ToList();
            if (path.Count > LimitAlong(path))
                return $"Folders can't go deeper than {LimitAlong(path)} levels here.";
        }
        return null;
    }

    /// <summary>Why <paramref name="name"/> can't name list <paramref name="list"/> (null: a new list in <paramref name="parent"/>), or null when it can.</summary>
    public string? CheckListName(string? list, string name, string? parent = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "A list needs a name.";
        var id = list is null ? null : ResolveList(list);
        var under = id is not null ? ParentOf(id) : parent is null ? null : ResolveList(parent);
        var siblings = under is null ? Top : Children(under);
        if (siblings.Any(s => s != id && Store.TryGetValue(s, out var other) && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            return under is null ? $"A list named '{name}' already exists." : $"'{Store[under].Name}' already has a folder named '{name}'.";
        return null;
    }

    /// <summary><paramref name="stem"/>, or "stem 2", "stem 3"… whichever no sibling uses yet (top level when <paramref name="parent"/> is null).</summary>
    public string UnusedListName(string stem, string? parent = null)
    {
        var siblings = (parent is null ? Top : Children(parent)).Where(Store.ContainsKey).Select(s => Store[s].Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var n = 2; siblings.Contains(name); n++)
            name = $"{stem} {n}";
        return name;
    }

    // ---- list editing --------------------------------------------------------------------------------------------

    /// <summary>Renames a list. Ids don't change, so keys, decks, folders and links follow. Returns why it was refused, or null.</summary>
    public string? RenameList(string? reference, string to)
    {
        to = to.Trim();
        var id = ResolveList(reference);
        if (!Store.TryGetValue(id, out var list))
            return $"There is no list '{reference}'.";
        if (CheckListName(id, to) is { } problem)
            return problem;
        if (list.Name == to)
            return null;
        if (Top.Contains(id) && !string.Equals(list.Name, to, StringComparison.OrdinalIgnoreCase))
            Renames[list.Name] = id;
        Renames.Remove(to);
        list.Name = to;
        return null;
    }

    /// <summary>The list's items, or an empty list when it doesn't exist. Use <see cref="EnsureList"/> to edit.</summary>
    public IReadOnlyList<Entry> GetList(string? reference) =>
        Store.TryGetValue(ResolveList(reference), out var list) ? list.Items : [];

    /// <summary>Creates an empty top-level list; a former name of another list stops pointing there.</summary>
    /// <returns>The new list's id, or null when a top-level list with that name already exists.</returns>
    public string? AddList(string name)
    {
        name = name.Trim();
        if (CheckListName(null, name) is not null)
            return null;
        var id = NewId();
        Store[id] = new StoredList { Name = name };
        Top.Add(id);
        Renames.Remove(name);
        return id;
    }

    /// <summary>The referenced list's items; a reference that resolves to nothing creates a top-level list with that name.</summary>
    public List<Entry> EnsureList(string? reference)
    {
        var id = ResolveList(reference);
        if (Store.TryGetValue(id, out var list))
            return list.Items;
        if (id == HomeList)
        {
            Store[HomeList] = list = new StoredList { Name = DefaultListName };
            Top.Insert(0, HomeList);
            return list.Items;
        }
        return Store[AddList(reference!)!].Items;
    }

    /// <summary>Adds an empty folder to a list. Returns why it was refused, or null.</summary>
    public string? AddFolder(string? parent, string name, out string id, out int index, int? at = null)
    {
        id = "";
        index = -1;
        var to = ResolveList(parent);
        if (!Store.TryGetValue(to, out var list))
            return $"There is no list '{parent}'.";
        if (list.Kind == ListKind.Dynamic)
            return FillsItself;
        if (CheckListName(null, name, to) is { } problem)
            return problem;
        if (DepthOf(to) + 1 > LimitOf(to))
            return $"Folders can't go deeper than {LimitOf(to)} levels here.";
        id = NewId();
        Store[id] = new StoredList { Name = name.Trim() };
        index = Math.Clamp(at ?? list.Items.Count, 0, list.Items.Count);
        list.Items.Insert(index, new Entry { Folder = id });
        return null;
    }

    /// <summary>Adds a link to <paramref name="target"/> into a list. Returns why it was refused, or null.</summary>
    public string? AddLink(string? list, string? target, out int index, int? at = null, string? title = null)
    {
        index = -1;
        if (IsDynamic(list))
            return FillsItself;
        if (CheckLink(list, target) is { } problem)
            return problem;
        var items = Store[ResolveList(list)].Items;
        index = Math.Clamp(at ?? items.Count, 0, items.Count);
        items.Insert(index, new Entry { Link = ResolveList(target), Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() });
        return null;
    }

    /// <summary>
    /// Copies a list with everything inside, folders included under new ids, so editing the copy leaves the source
    /// untouched. A top-level list's copy goes right after it at the top; a folder's copy right after its folder item.
    /// <paramref name="name"/> null or taken → "<c>source</c> copy", then "copy 2"… Returns the new id.
    /// </summary>
    public string CopyList(string? source, string? name = null)
    {
        var id = ResolveList(source);
        var parent = ParentOf(id);
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is null || CheckListName(null, name, parent) is not null)
            name = UnusedListName($"{NameOf(id)} copy", parent);

        var copy = DeepCopy(id);
        Store[copy].Name = name;
        if (parent is null)
        {
            var at = Top.IndexOf(id);
            Top.Insert(at < 0 ? Top.Count : at + 1, copy);
            Renames.Remove(name);
        }
        else
        {
            var items = Store[parent].Items;
            items.Insert(items.FindIndex(i => i.Folder == id) + 1, new Entry { Folder = copy });
        }
        return copy;
    }

    /// <summary>Copies a list and its folders under new ids; the copy has no owner yet.</summary>
    private string DeepCopy(string id)
    {
        var source = Store[id];
        var copy = new StoredList { Name = source.Name, Items = Clone(source.Items), Options = source.Options?.Clone() };
        var newId = NewId();
        Store[newId] = copy;
        foreach (var item in copy.Items.Where(i => i.IsFolder))
            item.Folder = DeepCopy(item.Folder!);
        return newId;
    }

    private static List<Entry> Clone(List<Entry> items) =>
        System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(
            System.Text.Json.JsonSerializer.Serialize(items, ConfigStore.JsonOptions), ConfigStore.JsonOptions) ?? [];

    /// <summary>
    /// Removes a list with all its folders, and its folder item or top-level place. Links, decks and old names that
    /// pointed into it are dropped. The home list can't be removed.
    /// </summary>
    public bool RemoveList(string? reference)
    {
        var id = ResolveList(reference);
        if (id == HomeList || !Store.ContainsKey(id))
            return false;
        if (ParentOf(id) is { } parent)
            Store[parent].Items.RemoveAll(i => i.Folder == id);
        Top.Remove(id);
        foreach (var gone in SubtreeOf(id).ToList())
            Store.Remove(gone);
        foreach (var list in Store.Values)
            list.Items.RemoveAll(i => i.Opens is { } opens && !Store.ContainsKey(opens));
        foreach (var deck in Decks.Where(d => !Store.ContainsKey(d.Value)).Select(d => d.Key).ToList())
            Decks.Remove(deck);
        foreach (var old in Renames.Where(r => !Store.ContainsKey(r.Value)).Select(r => r.Key).ToList())
            Renames.Remove(old);
        return true;
    }

    /// <summary>Changes a list's options; options left at their defaults are removed.</summary>
    public void UpdateOptions(string? reference, Action<ListOptions> change)
    {
        if (!Store.TryGetValue(StoredIdOf(reference), out var list))
            return;
        var options = list.Options ?? new ListOptions();
        change(options);
        list.Options = options.IsDefault ? null : options;
    }

    /// <summary>Options of the list (a generated folder: its auto-folder's); defaults when none are set or the list doesn't exist.</summary>
    public ListOptions OptionsOf(string? reference) =>
        Store.TryGetValue(StoredIdOf(reference), out var list) && list.Options is { } options ? options : new ListOptions();

    // ---- item editing --------------------------------------------------------------------------------------------

    /// <summary>Layout kinds of the list's items, in order; folders and links take a key like apps.</summary>
    public IReadOnlyList<Paging.ListItemKind> LayoutOf(string? reference) => GetList(reference).Select(e => e.LayoutKind).ToList();

    /// <summary>The app at <paramref name="index"/>; null past the end or for a break, folder or link.</summary>
    public Entry? GetApp(string? list, int index) => GetItem(list, index) is { IsApp: true } app ? app : null;

    /// <summary>The item at <paramref name="index"/>; null past the end.</summary>
    public Entry? GetItem(string? list, int index)
    {
        var items = GetList(list);
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    /// <returns>The index the entry ended up at.</returns>
    public int Insert(string? list, Entry entry, int? at = null)
    {
        var items = EnsureList(list);
        var index = Math.Clamp(at ?? items.Count, 0, items.Count);
        items.Insert(index, entry);
        return index;
    }

    /// <summary>Removes the item; a folder item takes its list and everything inside along.</summary>
    public bool Remove(string? list, int index)
    {
        if (GetItem(list, index) is not { } item)
            return false;
        if (item.IsFolder)
            return RemoveList(item.Folder);
        Store[ResolveList(list)].Items.RemoveAt(index);
        return true;
    }

    public bool Move(string? list, int from, int to)
    {
        var items = EnsureList(list);
        if (GetItem(list, from) is not { } entry || to < 0 || to >= items.Count)
            return false;
        items.RemoveAt(from);
        items.Insert(to, entry);
        return true;
    }

    /// <summary>
    /// Moves (or copies) the item at <paramref name="index"/> to the end of another list. A moved folder keeps its
    /// contents and id; a copied folder is a deep copy. Returns why it was refused, or null.
    /// </summary>
    public string? MoveItem(string? from, int index, string? to, bool copy)
    {
        var source = ResolveList(from);
        var target = ResolveList(to);
        if (GetItem(source, index) is not { } item)
            return "That item no longer exists.";
        if (!Store.TryGetValue(target, out var targetList))
            return "That list doesn't exist.";
        if (source == target)
            return "The item is already in that list.";
        if (targetList.Kind == ListKind.Dynamic || IsDynamic(source))
            return FillsItself;

        if (item.IsFolder)
        {
            var folder = item.Folder!;
            if (copy)
            {
                if (CheckDepth(target, folder) is { } copyProblem)
                    return copyProblem;
                var duplicate = DeepCopy(folder);
                Store[duplicate].Name = UnusedListName(Store[folder].Name, target);
                targetList.Items.Add(new Entry { Folder = duplicate });
                return null;
            }
            if (CheckFolderPlacement(target, folder) is { } problem)
                return problem;
            if (CheckListName(null, Store[folder].Name, target) is not null)
                Store[folder].Name = UnusedListName(Store[folder].Name, target);
        }
        else if (item.IsLink && CheckLink(target, item.Link) is { } linkProblem)
        {
            return linkProblem;
        }

        if (copy)
        {
            targetList.Items.Add(Clone([item])[0]);
        }
        else
        {
            Store[source].Items.RemoveAt(index);
            targetList.Items.Add(item);
        }
        return null;
    }
}
