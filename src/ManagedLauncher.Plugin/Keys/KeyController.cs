using System.Collections.Concurrent;
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
    private readonly AppRuntime runtime;

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

    public int PageOf(string device) => pages.GetValueOrDefault(device);

    /// <summary>The key's own list, else the list an Open key chose, else the deck's dedicated list, else <c>default</c>.</summary>
    public string ListOf(Key key)
    {
        var device = key.Device ?? "";
        return key.ListSetting ?? deviceLists.GetValueOrDefault(device) ?? config().Decks.GetValueOrDefault(device) ?? AppConfig.DefaultListName;
    }

    /// <summary>Back is the first key of the first page, only while the launcher was entered through an Open Launcher key.</summary>
    private LauncherLayout LayoutFor(string? device, string list)
    {
        var size = devices.GetValueOrDefault(device ?? "", DefaultSize);
        return new LauncherLayout(size.Keys, size.Columns, config().LayoutOf(list), openedFromProfile.GetValueOrDefault(device ?? ""));
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
                Fire(() => RefreshAsync(ev.Context, force: true));
                break;
            case "willDisappear":
                if (keys.TryRemove(ev.Context, out var gone) && gone.Action == SlotAction
                    && !keys.Values.Any(k => k.Device == gone.Device && k.Action == SlotAction))
                {
                    openedFromProfile[gone.Device ?? ""] = false;
                    deviceLists.TryRemove(gone.Device ?? "", out _);
                }
                tracker.Unsubscribe(ev.Context);
                timer.Cancel(ev.Context);
                break;
            case "didReceiveSettings":
                if (keys.TryGetValue(ev.Context, out var changed))
                {
                    changed.Update(ev);
                    await FollowRenameAsync(changed).ConfigureAwait(false);
                    await RefreshAsync(ev.Context, force: true).ConfigureAwait(false);
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
            case "propertyInspectorDidAppear":
            case "sendToPlugin":
                if (keys.TryGetValue(ev.Context, out var inspected))
                    await SendInfoAsync(inspected).ConfigureAwait(false);
                break;
        }
    }

    public async Task OnConfigChangedAsync()
    {
        await ApplyDecksAsync().ConfigureAwait(false);
        await Task.WhenAll(keys.Values.Select(FollowRenameAsync)).ConfigureAwait(false);
        await Task.WhenAll(keys.Keys.Select(c => RefreshAsync(c, force: true))).ConfigureAwait(false);
    }

    /// <summary>Points a key's List setting at the list's current name when the list it names was renamed.</summary>
    private async Task FollowRenameAsync(Key key)
    {
        var cfg = config();
        if (key.ListSetting is not { } list || cfg.Lists.ContainsKey(list))
            return;
        var current = cfg.ResolveList(list);
        if (!cfg.Lists.ContainsKey(current))
            return;
        log($"{key.Describe()}: list {list} renamed to {current}");
        await deck.SetSettingsAsync(key.Context, key.WithList(current)).ConfigureAwait(false);
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
        openedFromProfile[device] = false;
        pages[device] = 0;
        log($"device {device}: dedicated to list {list}");
        await deck.SwitchToProfileAsync(PluginContext, device, profile).ConfigureAwait(false);
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    /// <summary>What the key shows right now, given the app list and its device's page.</summary>
    internal KeyAssignment AssignmentFor(Key key)
    {
        if (key.Action != SlotAction)
            return new KeyAssignment(KeyRole.App, -1, 0, 1);
        var list = ListOf(key);
        if (key.FixedSlot is { } slot)
            return config().GetApp(list, slot) is not null ? new KeyAssignment(KeyRole.App, slot, 0, 1) : new KeyAssignment(KeyRole.Empty, -1, 0, 1);

        var size = devices.GetValueOrDefault(key.Device ?? "", DefaultSize);
        if (key.Column is not { } col || key.Row is not { } row)
            return new KeyAssignment(KeyRole.Empty, -1, 0, 1);
        return LayoutFor(key.Device, list).Assign(col + row * size.Columns, PageOf(key.Device ?? ""));
    }

    public Entry? EntryFor(Key key)
    {
        if (key.Action == SlotAction)
        {
            var assignment = AssignmentFor(key);
            return assignment.Role == KeyRole.App ? config().GetApp(ListOf(key), assignment.AppIndex) : null;
        }

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
        if (role == KeyRole.Manage)
        {
            timer.KeyDown(key.Context, threshold, () => Fire(() => ManageAsync(key)), null);
            return;
        }
        if (role == KeyRole.NextPage)
        {
            timer.KeyDown(key.Context, threshold,
                () => Fire(() => TurnPageAsync(key, +1)),
                key.IsInMultiAction ? null : () => Fire(() => TurnPageAsync(key, -1)));
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
        var layout = LayoutFor(device, ListOf(key));
        var page = layout.Step(PageOf(device), delta);
        pages[device] = page;
        log($"device {device}: page {page + 1}/{layout.PageCount}");
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    private Task RefreshDeviceAsync(string device) =>
        Task.WhenAll(keys.Values.Where(k => k.Device == device).Select(k => RefreshAsync(k.Context, force: false)));

    /// <summary>Shows the bundled launcher profile on this device with the key's list, starting at its first page.</summary>
    internal async Task OpenLauncherAsync(Key key)
    {
        var device = key.Device ?? "";
        var size = devices.GetValueOrDefault(device, DefaultSize);
        if (LauncherProfileFor(device, size) is not { } profile)
        {
            log($"no launcher profile for a {size.Columns}x{size.Rows} device");
            await deck.ShowAlertAsync(key.Context).ConfigureAwait(false);
            return;
        }
        var list = key.ListSetting ?? AppConfig.DefaultListName;
        deviceLists[device] = list;
        pages[device] = 0;
        openedFromProfile[device] = true;
        log($"device {device}: open launcher, list {list}");
        await deck.SwitchToProfileAsync(PluginContext, device, profile).ConfigureAwait(false);
        await RefreshDeviceAsync(device).ConfigureAwait(false);
    }

    internal async Task GoBackAsync(Key key)
    {
        var device = key.Device ?? "";
        openedFromProfile[device] = false;
        await deck.SwitchToProfileAsync(PluginContext, device, null).ConfigureAwait(false);
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
            await RefreshAsync(key.Context, force: false).ConfigureAwait(false);

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
            && cfg.OptionsOf(ListOf(key)).ReturnAfterLaunch)
            await ReturnFromLauncherAsync(key).ConfigureAwait(false);

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
            var list = config().ResolveList(key.ListSetting);
            await SetIfChangedAsync(key, null, null, ($"open {list}", images.Open(list)), force).ConfigureAwait(false);
            return;
        }
        if (key.Action == ManageAction)
        {
            await SetIfChangedAsync(key, null, null, ("manage", images.Manage), force).ConfigureAwait(false);
            return;
        }

        var assignment = AssignmentFor(key);
        if (assignment.Role == KeyRole.Back)
        {
            await SetIfChangedAsync(key, null, null, ("back", images.Back), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.Manage)
        {
            await SetIfChangedAsync(key, null, null, ("manage", images.Manage), force).ConfigureAwait(false);
            return;
        }
        if (assignment.Role == KeyRole.NextPage)
        {
            await SetIfChangedAsync(key, null, null,
                ($"next-page {assignment.Page}/{assignment.Pages}", images.NextPage(assignment.Page, assignment.Pages)), force).ConfigureAwait(false);
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

    private Task SendInfoAsync(Key key)
    {
        var cfg = config();
        var entry = EntryFor(key);
        var target = entry is null ? null : TargetResolver.Resolve(entry);
        var assignment = AssignmentFor(key);
        var payload = new JsonObject
        {
            ["configPath"] = AppPaths.ConfigFile,
            ["slot"] = key.FixedSlot,
            ["appIndex"] = assignment.AppIndex >= 0 ? assignment.AppIndex : null,
            ["role"] = assignment.Role.ToString().ToLowerInvariant(),
            ["page"] = assignment.Page + 1,
            ["pages"] = assignment.Pages,
            ["apps"] = cfg.GetList(ListOf(key)).Count,
            ["lists"] = new JsonArray(cfg.Lists.Keys.Order().Select(p => (JsonNode)p!).ToArray()),
            ["homeList"] = cfg.HomeList,
            ["list"] = ListOf(key),
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
