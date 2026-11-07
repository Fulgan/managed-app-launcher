using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManagedLauncher.Core;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Paging;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;
using ManagedLauncher.Plugin.StreamDeck;

namespace ManagedLauncher.Plugin.Keys;

/// <summary>
/// Owns every visible key: keeps its image and title in sync with the app list, the device's current page and
/// the running state, and turns presses into launches, focus, close or kill, or page turns. Every handler reports
/// failure on the key and in the log; nothing escapes to the event loop.
/// </summary>
public sealed class KeyController
{
    public const string SlotAction = "com.fulgan.managedapplauncher.slot";
    public const string LaunchAction = "com.fulgan.managedapplauncher.launch";
    public const string OpenAction = "com.fulgan.managedapplauncher.open";
    public const string ManageAction = "com.fulgan.managedapplauncher.manage";
    public const string AutoAction = "com.fulgan.managedapplauncher.auto";
    private static readonly DeviceSize DefaultSize = new(5, 3);

    private readonly IStreamDeck deck;
    private readonly Func<AppConfig> config;
    private readonly ProcessTracker tracker;
    private readonly PressTimer timer;
    private readonly IKeyImages images;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<string, Key> keys = new();
    private readonly ConcurrentDictionary<string, DeviceSize> devices = new();
    private readonly ConcurrentDictionary<string, int> deviceTypes = new();
    private readonly ConcurrentDictionary<string, int> pages = new();
    private readonly ConcurrentDictionary<string, string> deviceLists = new();
    private readonly ConcurrentDictionary<string, bool> openedFromProfile = new();
    private readonly ConcurrentDictionary<string, bool> connected = new();
    private readonly ConcurrentDictionary<string, string> appliedDecks = new();

    /// <summary>
    /// Folders and links opened on each device, innermost last; each frame remembers the page its parent was on.
    /// Empty means the device shows its root list.
    /// </summary>
    private readonly ConcurrentDictionary<string, ImmutableList<Frame>> folders = new();
    private readonly AppRuntime runtime;

    /// <summary>Most folders and links open at once on one device; guards against link chains that never end.</summary>
    internal const int MaxOpenFolders = 32;

    private sealed record Frame(string List, int ParentPage);

    /// <summary>Items of auto-folders (and folders generated in them) as last generated, by list id.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<Entry>> generated = new();

    /// <summary>Where auto-folders read installed and running apps from.</summary>
    public IAppSources Sources { get; set; } = new SystemAppSources();

    /// <summary>Runs an auto-folder generation; the plugin moves it to its STA thread, since shell enumeration needs one.</summary>
    public Func<Func<IReadOnlyList<Entry>>, Task<IReadOnlyList<Entry>>> RunGeneration { get; set; } = work => Task.Run(work);

    /// <summary>The items a list shows: stored items, or for an auto-folder the items generated when it was opened.</summary>
    internal IReadOnlyList<Entry> ItemsOf(string list) =>
        IsAuto(list) ? generated.GetValueOrDefault(list, []) : config().GetList(list);

    /// <summary>
    /// Lists defined by Auto List keys instead of the config, by id (<c>~</c> + hash of the key's source settings);
    /// folders generated in them use the usual <c>id/relative path</c> ids.
    /// </summary>
    private readonly ConcurrentDictionary<string, StoredList> keyLists = new();

    private StoredList? KeyListOf(string list) => keyLists.GetValueOrDefault(AutoFolders.Split(list).ListId);

    /// <summary>Whether the list's items are generated: an auto-folder, an Auto List key's list, or a folder generated in either.</summary>
    private bool IsAuto(string list) => KeyListOf(list) is not null || config().IsDynamic(list);

    private bool ListExists(string list) => KeyListOf(list) is not null || config().Exists(list);

    private Navigation NavigationOf(string list) => KeyListOf(list)?.Options?.Navigation ?? config().NavigationOf(list);

    /// <summary>Auto List keys repeat Back on every page unless their <c>backEveryPage</c> setting is false.</summary>
    private bool BackOnEveryPage(string list) =>
        KeyListOf(list) is { } keyList ? keyList.Options?.BackOnEveryPage ?? true : config().BackOnEveryPageOf(list);

    private ListOptions OptionsOf(string list) => KeyListOf(list) is not null ? new ListOptions() : config().OptionsOf(list);

    private SourceType? SourceTypeOf(string list) => (KeyListOf(list)?.Source ?? config().SourceOf(list))?.Type;

    private async Task GenerateAsync(string list)
    {
        var cfg = config();
        var keyList = KeyListOf(list);
        var sources = Sources;
        var items = await RunGeneration(() => keyList is null ? cfg.Generate(list, sources) : GenerateKeyList(keyList, list, sources)).ConfigureAwait(false);
        generated[list] = items;
    }

    /// <summary>An Auto List key's list is a top-level list: depth 1, nested folders within the default depth limit.</summary>
    private static IReadOnlyList<Entry> GenerateKeyList(StoredList keyList, string list, IAppSources sources)
    {
        var (root, nested) = AutoFolders.Split(list);
        var depth = 1 + (nested.Length == 0 ? 0 : nested.Count(c => c == '\\') + 1);
        return AutoFolders.Generate(keyList, root, nested, Math.Max(0, AppConfig.DefaultMaxDepth - depth), sources);
    }

    /// <summary>
    /// The list an Auto List key's settings describe: <c>source</c>, <c>path</c>, <c>types</c> (comma separated),
    /// <c>backEveryPage</c> (default true) and <c>navigation</c> (default arrows, since sources can give many pages).
    /// </summary>
    internal static (string Id, StoredList List) KeyListFor(Key key)
    {
        string? Text(string name) =>
            key.Settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        var type = Enum.TryParse<SourceType>(Text("source"), ignoreCase: true, out var parsed) ? parsed : SourceType.Steam;
        var source = new ListSource { Type = type };
        if (type == SourceType.Directory)
        {
            source.Path = Text("path");
            source.Types = Text("types")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        var signature = $"{type}|{source.Path}|{string.Join(",", source.Types ?? [])}".ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature)), 0, 4).ToLowerInvariant();
        var everyPage = Text("backEveryPage") != "false";
        var navigation = Enum.TryParse<Navigation>(Text("navigation"), ignoreCase: true, out var chosen) ? chosen : Navigation.Arrows;
        return ("~" + hash[1..], new StoredList
        {
            Kind = ListKind.Dynamic,
            Name = AutoFolders.Describe(source),
            Source = source,
            Options = new ListOptions { BackOnEveryPage = everyPage, Navigation = navigation, Theme = KeyColorOf(key) ?? KeyTheme.Grey },
        });
    }

    /// <summary>Lists some visible launcher key shows right now: opened folders, root lists and keys' own lists.</summary>
    private IEnumerable<string> ShownLists() =>
        keys.Values.Where(k => k.Action == SlotAction).Select(ListOf).Distinct().ToList();

    /// <summary>Generates an auto-folder that is about to be shown and has no items generated yet.</summary>
    private async Task EnsureGeneratedAsync(string list)
    {
        if (IsAuto(list) && !generated.ContainsKey(list))
            await GenerateAsync(list).ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates shown auto-folders whose source is running programs and repaints the devices showing them when
    /// their items changed; does nothing while none is shown.
    /// </summary>
    public async Task RefreshRunningFoldersAsync()
    {
        var cfg = config();
        foreach (var list in ShownLists().Where(l => IsAuto(l) && SourceTypeOf(l) == SourceType.Running).ToList())
        {
            var before = string.Join("|", ItemsOf(list).Select(e => e.Target));
            await GenerateAsync(list).ConfigureAwait(false);
            if (before == string.Join("|", ItemsOf(list).Select(e => e.Target)))
                continue;
            foreach (var key in keys.Values.Where(k => k.Action == SlotAction && ListOf(k) == list).GroupBy(k => k.Device ?? "").Select(g => g.First()).ToList())
            {
                var device = key.Device ?? "";
                pages[device] = LayoutFor(key).Clamp(PageOf(device));
                await RefreshDeviceAsync(device).ConfigureAwait(false);
            }
        }
    }

    public KeyController(
        IStreamDeck deck, Func<AppConfig> config, ProcessTracker tracker, PressTimer timer,
        PressExecutor executor, IKeyImages images, Action<string> log, TimeProvider? time = null)
    {
        this.deck = deck;
        this.config = config;
        this.tracker = tracker;
        this.timer = timer;
        this.images = images;
        this.log = log;
        this.time = time ?? TimeProvider.System;
        runtime = new AppRuntime(executor, this.time);
        tracker.StateChanged += (context, _) => Fire(() => RefreshAsync(context, force: false));
    }

    public readonly record struct DeviceSize(int Columns, int Rows)
    {
        public int Keys => Columns * Rows;
    }

    /// <summary>Reads device sizes from the <c>-info</c> registration argument.</summary>
    public void AddDevices(string infoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(infoJson);
            if (!doc.RootElement.TryGetProperty("devices", out var list) || list.ValueKind != JsonValueKind.Array)
                return;
            foreach (var device in list.EnumerateArray())
            {
                if (device.TryGetProperty("id", out var id) && id.GetString() is { } deviceId && ParseSize(device) is { } size)
                {
                    devices[deviceId] = size;
                    if (TryGetInt(device, "type", out var type))
                        deviceTypes[deviceId] = type;
                    connected[deviceId] = true;
                    Seen(deviceId, device, size);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            log($"bad registration info: {ex.Message}");
        }
    }

    /// <summary>Whether any of these processes shows a main window yet; ends the starting state.</summary>
    public Func<IReadOnlyCollection<int>, bool> HasWindow
    {
        get => runtime.HasWindow;
        set => runtime.HasWindow = value;
    }

    /// <summary>Opaque plugin id from registration; the context for requests not tied to a key.</summary>
    public string PluginContext { get; set; } = "";

    /// <summary>Called with each deck's name and size as it is seen, so the manager can list decks.</summary>
    public Action<KnownDeck>? DeckSeen { get; set; }

    private void Seen(string device, JsonElement info, DeviceSize size)
    {
        var name = info.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } text ? text : device;
        try
        {
            DeckSeen?.Invoke(new KnownDeck(device, name, size.Columns, size.Rows));
        }
        catch (Exception ex)
        {
            log($"could not record deck {device}: {ex.Message}");
        }
    }

    /// <summary>Starts the manager window and reports whether it started; invoked by the "Manage apps…" button in the key settings and by the Manage key.</summary>
    public Func<bool>? OpenManager { get; set; }

    /// <summary>Apps the property inspector can offer for a source (<see cref="AppCatalog"/>), off the event loop.</summary>
    public Func<string, Task<IReadOnlyList<AppChoice>>>? ListApps { get; set; }

    /// <summary>Icons of a file the property inspector can offer (<see cref="IconCatalog"/>), off the event loop.</summary>
    public Func<string, Task<IReadOnlyList<IconChoice>>>? ListIcons { get; set; }

    /// <summary>Icons per message to the property inspector; a whole Windows icon library at once makes one huge message.</summary>
    internal const int IconChunk = 48;

    public int PageOf(string device) => pages.GetValueOrDefault(device);

    /// <summary>
    /// Id of the list the key shows: its own list, else the innermost folder opened on its device, else the device's
    /// root list.
    /// </summary>
    public string ListOf(Key key)
    {
        if (key.ListSetting is { } own)
            return config().ResolveList(own);
        return folders.TryGetValue(key.Device ?? "", out var open) && open.Count > 0 ? open[^1].List : RootOf(key.Device ?? "");
    }

    /// <summary>The list an Open key chose, else the deck's dedicated list, else the home list.</summary>
    private string RootOf(string device)
    {
        var cfg = config();
        var root = cfg.ResolveList(deviceLists.GetValueOrDefault(device) ?? cfg.Decks.GetValueOrDefault(device));
        return cfg.Store.ContainsKey(root) || keyLists.ContainsKey(root) ? root : cfg.HomeList;
    }

    /// <summary>Whether the key follows its device into an opened folder.</summary>
    private bool InFolder(Key key) =>
        key.ListSetting is null && folders.TryGetValue(key.Device ?? "", out var open) && open.Count > 0;

    /// <summary>
    /// Inside an opened folder the first key of the first page is Up; at the root it is Back, only while the launcher
    /// was entered through an Open Launcher key.
    /// </summary>
    private LauncherLayout LayoutFor(Key key)
    {
        var device = key.Device ?? "";
        var size = devices.GetValueOrDefault(device, DefaultSize);
        var up = InFolder(key);
        var list = ListOf(key);
        return new LauncherLayout(size.Keys, size.Columns, ItemsOf(list).Select(e => e.LayoutKind).ToList(),
            up || openedFromProfile.GetValueOrDefault(device), up ? KeyRole.Up : KeyRole.Back, NavigationOf(list), BackOnEveryPage(list));
    }


    public async Task HandleAsync(StreamDeckEvent ev)
    {
        if (ev.Event == "deviceDidConnect" && ev.Device is not null)
        {
            if (ev.DeviceInfo is { ValueKind: JsonValueKind.Object } info && ParseSize(info) is { } size)
            {
                devices[ev.Device] = size;
                if (TryGetInt(info, "type", out var type))
                    deviceTypes[ev.Device] = type;
                Seen(ev.Device, info, size);
            }
            connected[ev.Device] = true;
            appliedDecks.TryRemove(ev.Device, out _);
            await ApplyDecksAsync().ConfigureAwait(false);
            return;
        }
        if (ev.Event == "deviceDidDisconnect" && ev.Device is not null)
        {
            connected.TryRemove(ev.Device, out _);
            return;
        }
        if (ev.Context is null)
            return;

        switch (ev.Event)
        {
            case "willAppear":
                var key = keys.AddOrUpdate(ev.Context, _ => Key.From(ev), (_, k) => k.Update(ev));
                tracker.Subscribe(ev.Context, snapshot => StateOf(key, snapshot));
                await FollowRenameAsync(key).ConfigureAwait(false);
                Fire(async () =>
                {
                    if (key.Action == SlotAction)
                        await EnsureGeneratedAsync(ListOf(key)).ConfigureAwait(false);
                    await RefreshAsync(ev.Context, force: true).ConfigureAwait(false);
                });
                break;
            case "willDisappear":
                if (keys.TryRemove(ev.Context, out var gone) && gone.Action == SlotAction
                    && !keys.Values.Any(k => k.Device == gone.Device && k.Action == SlotAction))
                {
                    openedFromProfile[gone.Device ?? ""] = false;
                    deviceLists.TryRemove(gone.Device ?? "", out _);
                    folders.TryRemove(gone.Device ?? "", out _);
                }
                tracker.Unsubscribe(ev.Context);
                timer.Cancel(ev.Context);
                break;
            case "didReceiveSettings":
                if (keys.TryGetValue(ev.Context, out var changed))
                {
                    changed.Update(ev);
                    var started = time.GetTimestamp();
                    await FollowRenameAsync(changed).ConfigureAwait(false);
                    await RefreshAsync(ev.Context, force: true).ConfigureAwait(false);
                    if (changed.Action == LaunchAction)
                        log($"{changed.Describe()}: settings → {EntryFor(changed)?.Target ?? "(none)"}, drawn in {time.GetElapsedTime(started).TotalMilliseconds:0} ms");
                    await SendInfoAsync(changed).ConfigureAwait(false);
                }
                break;
            case "keyDown":
                if (keys.TryGetValue(ev.Context, out var down))
                    OnKeyDown(down);
                break;
            case "keyUp":
                timer.KeyUp(ev.Context);
                break;
            case "sendToPlugin" when ev.Payload.ValueKind == JsonValueKind.Object
                && ev.Payload.TryGetProperty("request", out var request) && request.GetString() == "openManager":
                OpenManager?.Invoke();
                break;
            case "sendToPlugin" when ev.Payload.ValueKind == JsonValueKind.Object
                && ev.Payload.TryGetProperty("request", out var listRequest) && listRequest.GetString() == "listApps"
                && ev.Payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String:
                if (keys.TryGetValue(ev.Context, out var asking))
                    Fire(() => SendAppsAsync(asking, source.GetString()!));
                break;
            case "sendToPlugin" when ev.Payload.ValueKind == JsonValueKind.Object
                && ev.Payload.TryGetProperty("request", out var iconRequest) && iconRequest.GetString() == "listIcons":
                if (keys.TryGetValue(ev.Context, out var iconKey))
                {
                    var iconSource = ev.Payload.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "default";
                    var file = ev.Payload.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    var seq = TryGetInt(ev.Payload, "seq", out var n) ? n : 0;
                    Fire(() => SendIconsAsync(iconKey, iconSource, file, seq));
                }
                break;
            case "propertyInspectorDidAppear":
            case "sendToPlugin":
                if (keys.TryGetValue(ev.Context, out var inspected))
                    await SendInfoAsync(inspected).ConfigureAwait(false);
                break;
        }
    }

    public async Task OnConfigChangedAsync()
    {
        CloseRemovedFolders();
        foreach (var list in generated.Keys.Except(ShownLists()).ToList())
            generated.TryRemove(list, out _);
        await Task.WhenAll(ShownLists().Where(IsAuto).Select(GenerateAsync)).ConfigureAwait(false);
        await ApplyDecksAsync().ConfigureAwait(false);
        await Task.WhenAll(keys.Values.Select(FollowRenameAsync)).ConfigureAwait(false);
        await Task.WhenAll(keys.Keys.Select(c => RefreshAsync(c, force: true))).ConfigureAwait(false);
    }

    /// <summary>Leaves opened folders whose list no longer exists, back to the page their parent was on.</summary>
    private void CloseRemovedFolders()
    {
        var cfg = config();
        foreach (var (device, open) in folders)
        {
            var kept = open;
            while (kept.Count > 0 && !ListExists(kept[^1].List))
            {
                pages[device] = kept[^1].ParentPage;
                kept = kept.RemoveAt(kept.Count - 1);
            }
            if (kept != open)
                folders[device] = kept;
        }
    }

    /// <summary>Points a key's List setting at the list's id when it still carries a list name.</summary>
    private async Task FollowRenameAsync(Key key)
    {
        var cfg = config();
        if (key.ListSetting is not { } list)
            return;
        var id = cfg.ResolveList(list);
        if (id == list || !cfg.Store.ContainsKey(id))
            return;
        log($"{key.Describe()}: list {list} is {cfg.NameOf(id)} ({id})");
        await deck.SetSettingsAsync(key.Context, key.WithList(id)).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts each connected dedicated deck on the launcher once per connection and whenever its list changes, so the
    /// user can still leave the launcher on that deck until one of those happens again.
    /// </summary>
    private async Task ApplyDecksAsync()
    {
        var decks = config().Decks;
        foreach (var device in appliedDecks.Keys.Where(d => !decks.ContainsKey(d)).ToList())
            appliedDecks.TryRemove(device, out _);
        foreach (var (device, list) in decks)
        {
            if (!connected.ContainsKey(device) || appliedDecks.GetValueOrDefault(device) == list)
                continue;
            appliedDecks[device] = list;
            await ShowDeckListAsync(device, list).ConfigureAwait(false);
        }
    }

    private async Task ShowDeckListAsync(string device, string list)
    {
        var size = devices.GetValueOrDefault(device, DefaultSize);
        if (LauncherProfileFor(device, size) is not { } profile)
        {
            log($"device {device}: no launcher profile for a {size.Columns}x{size.Rows} device");
            return;
        }
        deviceLists.TryRemove(device, out _);
        folders.TryRemove(device, out _);
        openedFromProfile[device] = false;
        pages[device] = 0;
        log($"device {device}: dedicated to list {config().NameOf(list)}");
        await EnsureGeneratedAsync(config().ResolveList(list)).ConfigureAwait(false);
        await deck.SwitchToProfileAsync(PluginContext, device, profile).ConfigureAwait(false);
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    /// <summary>What the key shows right now, given the app list and its device's page.</summary>
    internal KeyAssignment AssignmentFor(Key key)
    {
        if (key.Action != SlotAction)
            return new KeyAssignment(KeyRole.App, -1, 0, 1);
        if (key.FixedSlot is { } slot)
            return ItemsOf(ListOf(key)) is var fixedItems && slot < fixedItems.Count && !fixedItems[slot].IsBreak ? new KeyAssignment(KeyRole.App, slot, 0, 1) : new KeyAssignment(KeyRole.Empty, -1, 0, 1);

        var size = devices.GetValueOrDefault(key.Device ?? "", DefaultSize);
        if (key.Column is not { } col || key.Row is not { } row)
            return new KeyAssignment(KeyRole.Empty, -1, 0, 1);
        return LayoutFor(key).Assign(col + row * size.Columns, PageOf(key.Device ?? ""));
    }

    /// <summary>The list item a slot key shows: an app, folder or link; null for control and empty keys.</summary>
    internal Entry? ItemFor(Key key)
    {
        if (key.Action != SlotAction)
            return null;
        var assignment = AssignmentFor(key);
        var items = ItemsOf(ListOf(key));
        return assignment.Role == KeyRole.App && assignment.AppIndex >= 0 && assignment.AppIndex < items.Count ? items[assignment.AppIndex] : null;
    }

    public Entry? EntryFor(Key key)
    {
        if (key.Action == SlotAction)
            return ItemFor(key) is { IsApp: true } app ? app : null;

        if (key.Settings is not { ValueKind: JsonValueKind.Object } settings)
            return null;
        var entry = settings.Deserialize<Entry>(ConfigStore.JsonOptions);
        return string.IsNullOrWhiteSpace(entry?.Target) ? null : entry;
    }

    internal RunState StateOf(Key key, ProcessSnapshot snapshot) =>
        EntryFor(key) is { } entry ? runtime.StateOf(entry, snapshot) : RunState.Stopped;

    private void OnKeyDown(Key key)
    {
        var threshold = TimeSpan.FromMilliseconds(config().LongPressMs);
        if (key.Action == OpenAction)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => OpenLauncherAsync(key)), null);
            return;
        }
        if (key.Action == AutoAction)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => OpenAutoListAsync(key)), null);
            return;
        }
        if (key.Action == ManageAction)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => ManageAsync(key)), null);
            return;
        }
        var role = key.Action == SlotAction ? AssignmentFor(key).Role : KeyRole.App;
        if (role == KeyRole.Empty)
            return;
        if (role == KeyRole.Back)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => GoBackAsync(key)), null);
            return;
        }
        if (role == KeyRole.Up)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => GoUpAsync(key)), null);
            return;
        }
        if (ItemFor(key) is { Opens: not null } folder)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => OpenFolderAsync(key, folder)), null);
            return;
        }
        if (role == KeyRole.Manage)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => ManageAsync(key)), null);
            return;
        }
        if (role is KeyRole.NextPage or KeyRole.PreviousPage)
        {
            var direction = role == KeyRole.NextPage ? +1 : -1;
            var arrows = LayoutFor(key).HasArrows;
            timer.KeyDown(key.Context, threshold,
                () => Fire(() => TurnPageAsync(key, direction)),
                key.IsInMultiAction ? null : () => Fire(() => TurnPageAsync(key, arrows ? direction * int.MaxValue / 2 : -1)));
            return;
        }

        var entry = EntryFor(key);
        var options = entry is null ? null : LaunchOptions.Resolve(entry);
        var hasLong = !key.IsInMultiAction && options?.LongPress == LongPressMode.Auto;
        timer.KeyDown(
            key.Context,
            threshold,
            () => Fire(() => ExecuteAsync(key, isLong: false)),
            hasLong ? () => Fire(() => ExecuteAsync(key, isLong: true)) : null);
    }

    internal async Task TurnPageAsync(Key key, int delta)
    {
        var device = key.Device ?? "";
        var layout = LayoutFor(key);
        var page = layout.HasArrows ? layout.Advance(PageOf(device), delta) : layout.Step(PageOf(device), delta);
        pages[device] = page;
        log($"device {device}: page {page + 1}/{layout.PageCount}");
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    private Task RefreshDeviceAsync(string device) =>
        Task.WhenAll(keys.Values.Where(k => k.Device == device).Select(k => RefreshAsync(k.Context, force: false)));

    /// <summary>Shows the bundled launcher profile on this device with the key's list, starting at its first page.</summary>
    internal Task OpenLauncherAsync(Key key) => OpenRootAsync(key, config().ResolveList(key.ListSetting));

    /// <summary>Shows the launcher with the apps of the key's source, read now.</summary>
    internal Task OpenAutoListAsync(Key key)
    {
        var (id, list) = KeyListFor(key);
        keyLists[id] = list;
        return OpenRootAsync(key, id);
    }

    private async Task OpenRootAsync(Key key, string list)
    {
        var device = key.Device ?? "";
        var size = devices.GetValueOrDefault(device, DefaultSize);
        if (LauncherProfileFor(device, size) is not { } profile)
        {
            log($"no launcher profile for a {size.Columns}x{size.Rows} device");
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
            return;
        }
        deviceLists[device] = list;
        folders.TryRemove(device, out _);
        pages[device] = 0;
        openedFromProfile[device] = true;
        log($"device {device}: open launcher, list {KeyListOf(list)?.Name ?? config().NameOf(list)}");
        if (IsAuto(list))
            await GenerateAsync(list).ConfigureAwait(false);
        await deck.SwitchToProfileAsync(PluginContext, device, profile).ConfigureAwait(false);
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    internal async Task GoBackAsync(Key key)
    {
        var device = key.Device ?? "";
        openedFromProfile[device] = false;
        await deck.SwitchToProfileAsync(PluginContext, device, null).ConfigureAwait(false);
    }

    /// <summary>Shows the list a folder or link key opens on the key's device, from its first page.</summary>
    internal async Task OpenFolderAsync(Key key, Entry item)
    {
        var cfg = config();
        var device = key.Device ?? "";
        var target = item.Opens!;
        var open = folders.GetValueOrDefault(device, []);
        var problem = item.LinkProblem
            ?? (!ListExists(target) ? "list removed" : open.Count >= MaxOpenFolders ? "too many folders open" : null);
        if (problem is not null)
        {
            log($"{key.Describe()}: can't open {cfg.TitleOf(item)}: {problem}");
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
            return;
        }
        if (IsAuto(target))
            await GenerateAsync(target).ConfigureAwait(false);
        folders[device] = open.Add(new Frame(target, PageOf(device)));
        pages[device] = 0;
        log($"device {device}: open {cfg.PathOf(target)}");
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    /// <summary>Leaves the innermost opened folder, back to the page its parent was on.</summary>
    internal async Task GoUpAsync(Key key)
    {
        var device = key.Device ?? "";
        if (!folders.TryGetValue(device, out var open) || open.Count == 0)
            return;
        folders[device] = open.RemoveAt(open.Count - 1);
        pages[device] = open[^1].ParentPage;
        log($"device {device}: up to {config().NameOf(folders[device].Count > 0 ? folders[device][^1].List : RootOf(device))}");
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    internal async Task ManageAsync(Key key)
    {
        if (OpenManager?.Invoke() == true)
            await deck.ShowOkAsync(key.Context).ConfigureAwait(false);
        else
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
    }

    internal async Task ExecuteAsync(Key key, bool isLong)
    {
        var cfg = config();
        var entry = EntryFor(key);
        if (entry is null)
        {
            log($"{key.Describe()}: nothing pinned");
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
            return;
        }

        var outcome = await runtime.PressAsync(entry, isLong, tracker.Refresh(), cfg, () => deck.ShowOkAsync(key.Context)).ConfigureAwait(false);
        var (decision, result) = (outcome.Decision, outcome.Result);
        log($"{key.Describe()}: {(isLong ? "long" : "short")} press, running={outcome.Running} → {decision}");
        if (outcome.Launched)
        {
            tracker.Refresh();
            await RefreshAsync(key.Context, force: false).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            log($"{key.Describe()}: {decision} failed: {result.Error}");
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
        }
        else if (!isLong && decision != PressDecision.Nothing)
        {
            await deck.ShowOkAsync(key.Context).ConfigureAwait(false);
        }

        if (result.Success && LeavesLauncher(decision) && key.Action == SlotAction && key.FixedSlot is null
            && OptionsOf(ListOf(key)).ReturnAfterLaunch)
        {
            if (InFolder(key))
                await GoUpAsync(key).ConfigureAwait(false);
            else
                await ReturnFromLauncherAsync(key).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(cfg.PollMs), time).ConfigureAwait(false);
        tracker.Refresh();
    }

    private static bool LeavesLauncher(PressDecision decision) => decision is PressDecision.Launch or PressDecision.LaunchAdmin
        or PressDecision.Focus or PressDecision.FocusOrRelaunch or PressDecision.FocusIfAny or PressDecision.Relaunch;

    /// <summary>Back to the profile the launcher was opened from, or to the first page when there is none.</summary>
    private async Task ReturnFromLauncherAsync(Key key)
    {
        var device = key.Device ?? "";
        if (openedFromProfile.GetValueOrDefault(device))
        {
            await GoBackAsync(key).ConfigureAwait(false);
            return;
        }
        if (PageOf(device) == 0)
            return;
        pages[device] = 0;
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    /// <summary>One refresh per key at a time, so a slower, older refresh can never paint over a newer one.</summary>
    private async Task RefreshAsync(string context, bool force)
    {
        if (!keys.TryGetValue(context, out var key) || key.IsInMultiAction)
            return;
        await key.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshLockedAsync(key, force).ConfigureAwait(false);
        }
        finally
        {
            key.Gate.Release();
        }
    }

    private async Task RefreshLockedAsync(Key key, bool force)
    {

        if (key.Action == OpenAction)
        {
            var cfg = config();
            var listId = cfg.ResolveList(key.ListSetting);
            var list = cfg.NameOf(listId);
            var openTheme = KeyColorOf(key) ?? cfg.ThemeOf(listId);
            var icon = ListIconOf(key);
            if (icon is not null || openTheme != KeyTheme.Grey)
            {
                var image = await images.OpenAsync(list, icon, openTheme).ConfigureAwait(false);
                await SetIfChangedAsync(key, null, null, ($"open {list}|{icon}|{openTheme}", image), force).ConfigureAwait(false);
            }
            else
            {
                await ResetImageAsync(key).ConfigureAwait(false);
            }
            return;
        }
        if (key.Action == ManageAction)
        {
            await SetIfChangedAsync(key, null, null, ("manage", images.Manage(KeyTheme.Grey)), force).ConfigureAwait(false);
            return;
        }
        if (key.Action == AutoAction)
        {
            if (KeyColorOf(key) is { } color and not KeyTheme.Grey)
                await SetIfChangedAsync(key, null, null, ($"auto {color}", images.AutoList(color)), force).ConfigureAwait(false);
            else
                await ResetImageAsync(key).ConfigureAwait(false);
            return;
        }

        var theme = ThemeOf(key);
        var assignment = AssignmentFor(key);
        if (assignment.Role == KeyRole.Back)
        {
            await SetIfChangedAsync(key, null, null, ($"back {theme}", images.Back(theme)), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.Up)
        {
            await SetIfChangedAsync(key, null, null, ($"up {theme}", images.Up(theme)), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.Manage)
        {
            await SetIfChangedAsync(key, null, null, ($"manage {theme}", images.Manage(theme)), force).ConfigureAwait(false);
            return;
        }
        if (ItemFor(key) is { Opens: { } opens } folder)
        {
            var cfg = config();
            var name = cfg.TitleOf(folder);
            if (folder.LinkProblem is not null)
            {
                await SetIfChangedAsync(key, null, null, ($"broken {name}|{theme}", images.BrokenLink(name, theme)), force).ConfigureAwait(false);
                return;
            }
            var icon = !AutoFolders.IsNested(opens) && cfg.OptionsOf(opens).Icon is { Length: > 0 } custom ? TargetResolver.Expand(custom) : null;
            var image = await images.FolderAsync(name, icon, folder.IsLink, theme).ConfigureAwait(false);
            await SetIfChangedAsync(key, null, null, ($"folder {name}|{icon}|{folder.IsLink}|{theme}", image), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.PreviousPage)
        {
            await SetIfChangedAsync(key, null, null,
                ($"previous-page {assignment.Page}/{assignment.Pages}|{theme}", images.PreviousPage(assignment.Page, assignment.Pages, theme)), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.NextPage)
        {
            await SetIfChangedAsync(key, null, null,
                ($"next-page {assignment.Page}/{assignment.Pages}|{theme}", images.NextPage(assignment.Page, assignment.Pages, theme)), force).ConfigureAwait(false);
            return;
        }

        var entry = EntryFor(key);
        if (entry is null)
        {
            var fixedImage = key.Action == SlotAction ? ("blank", images.Blank) : default((string, string)?);
            await SetIfChangedAsync(key, null, null, fixedImage, force).ConfigureAwait(false);
            return;
        }

        var target = TargetResolver.Resolve(entry);
        var options = LaunchOptions.Resolve(entry);
        var visual = new KeyVisual(target.IconSource, runtime.StateOf(entry, target, tracker.Current), options.Indicator, !target.IsLaunchable);
        var customIcon = entry.Icon is null ? null : TargetResolver.Expand(entry.Icon);
        await SetIfChangedAsync(key, entry.Title, visual, null, force, customIcon).ConfigureAwait(false);
    }

    /// <param name="fixedImage">Named prerendered image used instead of <paramref name="visual"/> (next-page, blank).</param>
    private async Task SetIfChangedAsync(Key key, string? title, KeyVisual? visual, (string Name, string Png)? fixedImage, bool force, string? customIcon = null)
    {
        var look = new Look(visual, fixedImage?.Name);
        if (force || look != key.LastLook)
        {
            var image = fixedImage?.Png ?? (visual is null ? null : await images.RenderAsync(visual, customIcon).ConfigureAwait(false));
            await deck.SetImageAsync(key.Context, image).ConfigureAwait(false);
            key.LastLook = look;
        }
        if (force || title != key.LastTitle)
        {
            await deck.SetTitleAsync(key.Context, title).ConfigureAwait(false);
            key.LastTitle = title;
        }
    }

    /// <summary>Tells an Auto List key's panel how many items its source gives right now.</summary>
    private async Task SendAutoInfoAsync(Key key)
    {
        var (id, list) = KeyListFor(key);
        var payload = new JsonObject { ["configPath"] = AppPaths.ConfigFile };
        if (list.Source is { Type: SourceType.Directory } source && (source.Path is null || !Directory.Exists(TargetResolver.Expand(source.Path))))
        {
            payload["apps"] = 0;
            payload["error"] = source.Path is null ? "choose a directory" : $"{source.Path} does not exist";
        }
        else
        {
            var sources = Sources;
            payload["apps"] = (await RunGeneration(() => GenerateKeyList(list, id, sources)).ConfigureAwait(false)).Count;
        }
        await deck.SendToPropertyInspectorAsync(key.Action, key.Context, payload).ConfigureAwait(false);
    }

    private Task SendInfoAsync(Key key)
    {
        if (key.Action == AutoAction)
            return SendAutoInfoAsync(key);
        var cfg = config();
        var entry = EntryFor(key);
        var target = entry is null ? null : TargetResolver.Resolve(entry);
        var assignment = AssignmentFor(key);
        var item = ItemFor(key);
        var payload = new JsonObject
        {
            ["configPath"] = AppPaths.ConfigFile,
            ["slot"] = key.FixedSlot,
            ["appIndex"] = assignment.AppIndex >= 0 ? assignment.AppIndex : null,
            ["role"] = assignment.Role.ToString().ToLowerInvariant(),
            ["page"] = assignment.Page + 1,
            ["pages"] = assignment.Pages,
            ["apps"] = ItemsOf(ListOf(key)).Count,
            ["lists"] = new JsonArray(cfg.Tree().Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id,
                ["name"] = cfg.NameOf(t.Id),
                ["depth"] = t.Depth,
                ["auto"] = cfg.IsDynamic(t.Id),
            }).ToArray()),
            ["homeList"] = cfg.HomeList,
            ["homeName"] = cfg.NameOf(cfg.HomeList),
            ["list"] = ListOf(key),
            ["listName"] = cfg.PathOf(ListOf(key)),
            ["opens"] = item?.Opens is null ? null : new JsonObject
            {
                ["kind"] = item.IsFolder ? "folder" : "link",
                ["name"] = cfg.PathOf(item.Opens),
                ["problem"] = item.LinkProblem,
            },
            ["entry"] = entry is null ? null : JsonSerializer.SerializeToNode(entry, ConfigStore.JsonOptions),
            ["resolved"] = target is null ? null : new JsonObject
            {
                ["kind"] = target.Kind.ToString().ToLowerInvariant(),
                ["launch"] = target.LaunchFile ?? target.Aumid,
                ["match"] = target.MatchPath ?? target.Aumid,
                ["error"] = target.Error,
            },
        };
        return deck.SendToPropertyInspectorAsync(key.Action, key.Context, payload);
    }

    private async Task SendAppsAsync(Key key, string source)
    {
        var payload = new JsonObject { ["source"] = source };
        try
        {
            var apps = ListApps is null ? [] : await ListApps(source).ConfigureAwait(false);
            payload["apps"] = new JsonArray(apps.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["detail"] = a.Detail,
                ["entry"] = JsonSerializer.SerializeToNode(a.Entry, ConfigStore.JsonOptions),
            }).ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"list {source} apps failed: {ex.Message}");
            payload["apps"] = new JsonArray();
            payload["error"] = ex.Message;
        }
        await deck.SendToPropertyInspectorAsync(key.Action, key.Context, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the icons of the requested file in chunks. Sources: <c>app</c> = the key's program file, <c>windows</c> =
    /// the Windows icon library, <c>file</c> = a path, <c>default</c> = the current custom icon's file, else app, else windows.
    /// </summary>
    private async Task SendIconsAsync(Key key, string source, string? path, int seq)
    {
        var entry = EntryFor(key);
        var appFile = entry is null ? null : AppIconFile(entry);
        var current = entry?.Icon is { } icon ? IconExtractor.ParseIconRef(TargetResolver.Expand(icon)) : default((string File, int? Index)?);
        var file = source switch
        {
            "app" => appFile,
            "windows" => IconCatalog.WindowsIcons,
            "file" => path is null ? null : TargetResolver.Expand(path),
            _ => current?.File ?? appFile ?? IconCatalog.WindowsIcons,
        };

        JsonObject Reply(int offset, int total, bool done) => new()
        {
            ["seq"] = seq,
            ["iconFile"] = file,
            ["appFile"] = appFile,
            ["selected"] = current is { } c && string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase) ? c.Index ?? -1 : null,
            ["offset"] = offset,
            ["total"] = total,
            ["done"] = done,
        };

        IReadOnlyList<IconChoice> icons;
        try
        {
            icons = file is null || ListIcons is null ? [] : await ListIcons(file).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"list icons of {file} failed: {ex.Message}");
            var failed = Reply(0, 0, true);
            failed["icons"] = new JsonArray();
            failed["error"] = ex.Message;
            await deck.SendToPropertyInspectorAsync(key.Action, key.Context, failed).ConfigureAwait(false);
            return;
        }

        var offset = 0;
        do
        {
            var chunk = icons.Skip(offset).Take(IconChunk).ToList();
            var reply = Reply(offset, icons.Count, offset + chunk.Count >= icons.Count);
            reply["icons"] = new JsonArray(chunk.Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i.Index,
                ["png"] = Convert.ToBase64String(i.Png),
            }).ToArray());
            if (file is null)
                reply["error"] = "this app has no program file to take icons from";
            await deck.SendToPropertyInspectorAsync(key.Action, key.Context, reply).ConfigureAwait(false);
            offset += chunk.Count;
        } while (offset < icons.Count);
    }

    /// <summary>Tile colour of the keys a slot key's list shows: an Auto List key's colour, else the list's theme.</summary>
    private KeyTheme ThemeOf(Key key)
    {
        var list = ListOf(key);
        return KeyListOf(list)?.Options?.Theme ?? config().ThemeOf(list);
    }

    /// <summary>The <c>color</c> setting of an Open Launcher or Auto List key; null when unset (default).</summary>
    private static KeyTheme? KeyColorOf(Key key) =>
        key.Settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("color", out var v) && v.ValueKind == JsonValueKind.String
            && Enum.TryParse<KeyTheme>(v.GetString(), ignoreCase: true, out var theme) ? theme : null;

    /// <summary>Gives the key back to Stream Deck's own image (the manifest's or the user's) after the plugin painted it.</summary>
    private async Task ResetImageAsync(Key key)
    {
        if (key.LastLook is null)
            return;
        await deck.SetImageAsync(key.Context, null).ConfigureAwait(false);
        key.LastLook = null;
    }

    private string? ListIconOf(Key key) =>
        config().OptionsOf(key.ListSetting).Icon is { } icon && !string.IsNullOrWhiteSpace(icon) ? TargetResolver.Expand(icon) : null;

    /// <summary>The program file whose icons the key's app offers: the exe behind a shortcut, else the launched file.</summary>
    private static string? AppIconFile(Entry entry)
    {
        var resolved = TargetResolver.Resolve(entry);
        var file = resolved.ShortcutExe ?? resolved.LaunchFile;
        return file is not null && File.Exists(file) ? file : null;
    }

    /// <summary>The bundled launcher profile for this device: by its type, else by its key layout; null when none fits.</summary>
    private string? LauncherProfileFor(string device, DeviceSize size) =>
        LauncherProfiles.For(deviceTypes.TryGetValue(device, out var type) ? type : null, size.Columns, size.Rows)?.ManifestName;

    private static DeviceSize? ParseSize(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Object
        && TryGetInt(size, "columns", out var columns) && TryGetInt(size, "rows", out var rows) && columns > 0 && rows > 0
            ? new DeviceSize(columns, rows)
            : null;

    /// <summary>Integer property, or false for a missing or non-numeric one; never throws.</summary>
    internal static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    private readonly ConcurrentDictionary<Task, byte> running = new();

    /// <summary>Runs work off the event loop; failures are logged, never thrown.</summary>
    private void Fire(Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"handler failed: {ex}");
            }
        });
        running[task] = 0;
        _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes once no fired work is left, including work that fired work started.</summary>
    internal async Task IdleAsync()
    {
        while (!running.IsEmpty)
            await Task.WhenAll(running.Keys).ConfigureAwait(false);
    }

    internal sealed record Look(KeyVisual? Visual, string? Fixed);

    /// <summary>
    /// A visible key. Event data is kept in one immutable record swapped atomically, because the event loop updates
    /// it while the tracker and refreshes read it from other threads.
    /// </summary>
    public sealed class Key
    {
        private sealed record State(string? Device, JsonElement? Settings, bool IsInMultiAction, int? Column, int? Row);

        private volatile State state = new(null, null, false, null, null);

        public required string Context { get; init; }
        public required string Action { get; init; }
        public string? Device => state.Device;
        public JsonElement? Settings => state.Settings;
        public bool IsInMultiAction => state.IsInMultiAction;
        public int? Column => state.Column;
        public int? Row => state.Row;
        internal Look? LastLook { get; set; }
        internal string? LastTitle { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Explicit <c>list</c> setting, or null when the key follows its device (see <see cref="ListOf"/>).</summary>
        public string? ListSetting =>
            Settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("list", out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        /// <summary>Explicit <c>slot</c> setting: the key always shows that app and takes no part in paging.</summary>
        public int? FixedSlot =>
            Settings is { } s && TryGetInt(s, "slot", out var slot) && slot >= 0
                ? slot
                : null;

        /// <summary>Replaces the <c>list</c> setting, keeping the others; returns the new settings to send to Stream Deck.</summary>
        public JsonNode WithList(string list)
        {
            var node = Settings is { ValueKind: JsonValueKind.Object } s ? JsonNode.Parse(s.GetRawText())! : new JsonObject();
            node["list"] = list;
            state = state with { Settings = JsonSerializer.SerializeToElement(node) };
            return node;
        }

        public static Key From(StreamDeckEvent ev) =>
            new Key { Context = ev.Context!, Action = ev.Action ?? "" }.Update(ev);

        public Key Update(StreamDeckEvent ev)
        {
            var next = state with { Device = ev.Device ?? state.Device };
            if (ev.Settings is { } settings)
                next = next with { Settings = settings };
            if (ev.Payload.ValueKind == JsonValueKind.Object)
            {
                next = next with { IsInMultiAction = ev.IsInMultiAction };
                if (ev.Payload.TryGetProperty("coordinates", out var coords)
                    && TryGetInt(coords, "column", out var column) && TryGetInt(coords, "row", out var row))
                    next = next with { Column = column, Row = row };
            }
            state = next;
            return this;
        }

        public string Describe() => $"key {Column},{Row}";
    }
}
