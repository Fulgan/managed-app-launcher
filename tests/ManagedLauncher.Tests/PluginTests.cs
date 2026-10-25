using System.Text.Json.Nodes;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;
using ManagedLauncher.Plugin.Keys;
using ManagedLauncher.Plugin.StreamDeck;
using Microsoft.Extensions.Time.Testing;

namespace ManagedLauncher.Tests;

public class PluginTests
{
    private const string Device = "dev1";

    private static StreamDeckEvent Event(string ev, string context, string action, string payload = "{}") =>
        StreamDeckEvent.Parse($$"""{"event":"{{ev}}","context":"{{context}}","action":"{{action}}","device":"{{Device}}","payload":{{payload}}}""");

    private static StreamDeckEvent Appear(string context, int column, int row, string settings = "{}", string action = KeyController.SlotAction) =>
        Event("willAppear", context, action, $$"""{"settings":{{settings}},"coordinates":{"column":{{column}},"row":{{row}}},"isInMultiAction":false}""");

    private sealed class Harness : IDisposable
    {
        public TempDir Tmp { get; } = new();
        public FakeDeck Deck { get; } = new();
        public FakeImages Images { get; } = new();
        public FakeSource Source { get; } = new();
        public FakeOperations Operations { get; } = new();
        public AppConfig Config { get; set; } = new AppConfig { PollMs = 1 }.Normalize();
        public ProcessTracker Tracker { get; }
        public KeyController Controller { get; }

        public Harness()
        {
            Tracker = new ProcessTracker(Source);
            Controller = new KeyController(Deck, () => Config, Tracker, new PressTimer(new FakeTimeProvider()),
                new PressExecutor(Operations), Images, _ => { });
            Controller.AddDevices($$"""{"devices":[{"id":"{{Device}}","size":{"columns":5,"rows":3},"type":0}]}""");
            Controller.PluginContext = "plugin-uuid";
        }

        /// <summary>Handles the event and waits for the work it fired, such as the key's first refresh.</summary>
        public async Task HandleAsync(StreamDeckEvent ev)
        {
            await Controller.HandleAsync(ev);
            await Controller.IdleAsync();
        }

        public string AddApp(string name)
        {
            var exe = Tmp.Touch(name + ".exe");
            Config.EnsureList(null).Add(new Entry { Target = exe });
            return exe;
        }

        public void Dispose() => Tmp.Dispose();
    }

    [Fact]
    public void KeyIndexUsesDeviceColumns()
    {
        using var h = new Harness();
        for (var i = 0; i < 8; i++)
            h.AddApp($"app{i}");
        var key = KeyController.Key.From(Appear("ctx", column: 2, row: 1));

        Assert.Equal(7, h.Controller.AssignmentFor(key).AppIndex);
    }

    [Fact]
    public async Task AppKeysHaveNoTitleUnlessSet()
    {
        using var h = new Harness();
        h.AddApp("a");
        h.Config.EnsureList(null).Add(new Entry { Target = h.Tmp.Touch("b.exe"), Title = "Bee" });

        await h.HandleAsync(Appear("k0", 0, 0));
        await h.HandleAsync(Appear("k1", 1, 0));

        Assert.Contains(("title", "k0", null), h.Deck.Calls);
        Assert.Contains(("title", "k1", "Bee"), h.Deck.Calls);
    }

    [Fact]
    public async Task OverflowShowsNextPageKeyAndTurnsPages()
    {
        using var h = new Harness();
        for (var i = 0; i < 16; i++)
            h.AddApp($"app{i:00}");
        await h.HandleAsync(Appear("first", 0, 0));
        await h.HandleAsync(Appear("nav", 4, 2));

        Assert.Equal("next 1/2", FindIcon(h, "nav"));
        Assert.Contains(("title", "nav", null), h.Deck.Calls);
        Assert.EndsWith("app00.exe", FindIcon(h, "first"));

        var nav = KeyController.Key.From(Appear("nav", 4, 2));
        await h.Controller.TurnPageAsync(nav, +1);

        Assert.Equal(1, h.Controller.PageOf(Device));
        Assert.Equal("next 2/2", FindIcon(h, "nav"));
        Assert.EndsWith("app14.exe", FindIcon(h, "first"));

        await h.Controller.TurnPageAsync(nav, +1);
        Assert.Equal(0, h.Controller.PageOf(Device));
    }

    private static string FindIcon(Harness h, string context) => h.Deck.LastImage(context) ?? "";

    [Fact]
    public async Task ListSettingSelectsNamedList()
    {
        using var h = new Harness();
        h.AddApp("main");
        h.Config.EnsureList("dev").Add(new Entry { Target = h.Tmp.Touch("devtool.exe") });

        await h.HandleAsync(Appear("ctx", 0, 0, settings: """{"list":"dev"}"""));

        Assert.EndsWith("devtool.exe", FindIcon(h, "ctx"));
    }

    [Fact]
    public async Task OpenSwitchesToLauncherWithListAndShowsBack()
    {
        using var h = new Harness();
        h.AddApp("main");
        for (var i = 0; i < 3; i++)
            h.Config.EnsureList("dev").Add(new Entry { Target = h.Tmp.Touch($"dev{i}.exe") });
        var opener = KeyController.Key.From(Appear("opener", 0, 0, settings: """{"list":"dev"}""", action: KeyController.OpenAction));

        await h.Controller.OpenLauncherAsync(opener);
        await h.HandleAsync(Appear("back", 0, 0));
        await h.HandleAsync(Appear("k1", 1, 0));
        await h.HandleAsync(Appear("k10", 0, 2));

        Assert.Contains(("switch", Device, "profiles/Managed App Launcher"), h.Deck.Calls);
        Assert.Equal("back", FindIcon(h, "back"));
        Assert.EndsWith("dev0.exe", FindIcon(h, "k1"));
        Assert.Equal("blank", FindIcon(h, "k10"));

        await h.Controller.GoBackAsync(KeyController.Key.From(Appear("back", 0, 0)));
        Assert.Contains(("switch", Device, null), h.Deck.Calls);
        Assert.All(h.Deck.SwitchContexts, c => Assert.Equal("plugin-uuid", c));
    }

    private static StreamDeckEvent Connect(string device = Device) =>
        StreamDeckEvent.Parse($$$"""{"event":"deviceDidConnect","device":"{{{device}}}","deviceInfo":{"name":"Desk","size":{"columns":5,"rows":3},"type":0}}""");

    [Fact]
    public async Task DedicatedDeckShowsItsListAndSwitchesOnConnect()
    {
        using var h = new Harness();
        h.AddApp("main");
        h.Config.EnsureList("games").Add(new Entry { Target = h.Tmp.Touch("game.exe") });
        h.Config.Decks[Device] = "games";
        h.Controller.PluginContext = "plugin";

        await h.HandleAsync(Connect());
        await h.HandleAsync(Appear("k0", 0, 0));
        await h.HandleAsync(Appear("k10", 0, 2));

        Assert.Single(h.Deck.Calls, c => c == ("switch", Device, "profiles/Managed App Launcher"));
        Assert.EndsWith("game.exe", FindIcon(h, "k0"));
        Assert.NotEqual("back", FindIcon(h, "k10"));
    }

    [Fact]
    public async Task DedicatingDeckInConfigSwitchesOnceAndFreeingDoesNot()
    {
        using var h = new Harness();
        h.Config.EnsureList("games");
        await h.HandleAsync(Connect());
        Assert.DoesNotContain(h.Deck.Calls, c => c.Item1 == "switch");

        h.Config.Decks[Device] = "games";
        await h.Controller.OnConfigChangedAsync();
        await h.Controller.OnConfigChangedAsync();
        Assert.Single(h.Deck.Calls, c => c.Item1 == "switch");

        h.Config.Decks.Remove(Device);
        await h.Controller.OnConfigChangedAsync();
        Assert.Single(h.Deck.Calls, c => c.Item1 == "switch");
    }

    [Fact]
    public async Task DisconnectedDeckIsNotSwitched()
    {
        using var h = new Harness();
        h.Config.EnsureList("games");
        await h.HandleAsync(StreamDeckEvent.Parse($$"""{"event":"deviceDidDisconnect","device":"{{Device}}"}"""));

        h.Config.Decks[Device] = "games";
        await h.Controller.OnConfigChangedAsync();

        Assert.DoesNotContain(h.Deck.Calls, c => c.Item1 == "switch");
    }

    [Fact]
    public async Task OpenListOverridesDeckListUntilLauncherLeaves()
    {
        using var h = new Harness();
        h.Config.EnsureList("games").Add(new Entry { Target = h.Tmp.Touch("game.exe") });
        h.Config.EnsureList("dev").Add(new Entry { Target = h.Tmp.Touch("tool.exe") });
        h.Config.Decks[Device] = "games";
        var opener = KeyController.Key.From(Appear("opener", 0, 0, settings: """{"list":"dev"}""", action: KeyController.OpenAction));

        await h.Controller.OpenLauncherAsync(opener);
        await h.HandleAsync(Appear("k1", 1, 0));
        Assert.EndsWith("tool.exe", FindIcon(h, "k1"));

        await h.HandleAsync(Event("willDisappear", "k1", KeyController.SlotAction));
        await h.HandleAsync(Appear("k0", 0, 0));
        Assert.EndsWith("game.exe", FindIcon(h, "k0"));
    }

    [Fact]
    public async Task RowBreakShiftsAppsToNextRowAndFixedSlotOnBreakIsEmpty()
    {
        using var h = new Harness();
        h.AddApp("a");
        h.Config.EnsureList(null).Add(new Entry { Break = ListBreak.Row });
        h.AddApp("b");

        await h.HandleAsync(Appear("r1", 0, 1));
        await h.HandleAsync(Appear("k1", 1, 0));
        await h.HandleAsync(Appear("fixed", 2, 2, settings: """{"slot":1}"""));

        Assert.EndsWith("b.exe", FindIcon(h, "r1"));
        Assert.Equal("blank", FindIcon(h, "k1"));
        Assert.Equal("blank", FindIcon(h, "fixed"));
    }

    [Theory]
    [InlineData(7, "profiles/Managed App Launcher Plus")]
    [InlineData(9, "profiles/Managed App Launcher Neo")]
    public async Task SameSizedDecksGetTheirOwnLauncherByType(int type, string profile)
    {
        using var h = new Harness();
        await h.HandleAsync(StreamDeckEvent.Parse(
            $$$"""{"event":"deviceDidConnect","device":"d","deviceInfo":{"name":"x","size":{"columns":4,"rows":2},"type":{{{type}}}}}"""));
        var opener = KeyController.Key.From(StreamDeckEvent.Parse(
            $$$"""{"event":"willAppear","context":"o","action":"{{{KeyController.OpenAction}}}","device":"d","payload":{"settings":{} } }"""));

        await h.Controller.OpenLauncherAsync(opener);

        Assert.Contains(("switch", "d", profile), h.Deck.Calls);
    }

    [Fact]
    public async Task MalformedDeviceInfoIsIgnored()
    {
        using var h = new Harness();

        h.Controller.AddDevices("""{"devices":[{"id":"x","size":{"columns":"five","rows":3}},{"id":"y","size":7},{"size":{}}]}""");
        await h.HandleAsync(StreamDeckEvent.Parse("""{"event":"deviceDidConnect","device":"z","deviceInfo":{"size":{"columns":-1,"rows":2}}}"""));
        await h.HandleAsync(Event("willAppear", "k", KeyController.SlotAction, """{"coordinates":{"column":"a"}}"""));
        await h.HandleAsync(Appear("s", 0, 0, settings: """{"slot":"3"}"""));
    }

    [Fact]
    public async Task HotPluggedDeckSizeComesFromRootDeviceInfo()
    {
        using var h = new Harness();
        await h.HandleAsync(StreamDeckEvent.Parse(
            """{"event":"deviceDidConnect","device":"xl","deviceInfo":{"name":"XL","size":{"columns":8,"rows":4},"type":2}}"""));
        var opener = KeyController.Key.From(StreamDeckEvent.Parse(
            $$$"""{"event":"willAppear","context":"o","action":"{{{KeyController.OpenAction}}}","device":"xl","payload":{"settings":{} } }"""));

        await h.Controller.OpenLauncherAsync(opener);

        Assert.Contains(("switch", "xl", "profiles/Managed App Launcher XL"), h.Deck.Calls);
    }

    [Fact]
    public async Task KeyNamingRenamedListShowsItAndGetsSettingRewritten()
    {
        using var h = new Harness();
        h.Config.EnsureList("work").Add(new Entry { Target = h.Tmp.Touch("tool.exe") });
        h.Config.RenameList("work", "office");

        await h.HandleAsync(Appear("k0", 0, 0, settings: """{"list":"work","slot":0}"""));

        Assert.EndsWith("tool.exe", FindIcon(h, "k0"));
        var settings = Assert.Single(h.Deck.Calls, c => c.Item1 == "settings").Item3!;
        Assert.Equal("""{"list":"office","slot":0}""", settings);
    }

    [Fact]
    public async Task OpenKeyImageShowsItsList()
    {
        using var h = new Harness();
        h.Config.EnsureList("games");

        await h.HandleAsync(Appear("o1", 0, 0, settings: """{"list":"games"}""", action: KeyController.OpenAction));
        await h.HandleAsync(Appear("o2", 1, 0, action: KeyController.OpenAction));

        Assert.Equal("open games", FindIcon(h, "o1"));
        Assert.Equal("open default", FindIcon(h, "o2"));
        Assert.Contains(("title", "o1", null), h.Deck.Calls);
    }

    [Fact]
    public async Task ManageActionShowsGearAndOpensManager()
    {
        using var h = new Harness();
        var opened = 0;
        h.Controller.OpenManager = () => ++opened > 0;

        await h.HandleAsync(Appear("m", 0, 0, action: KeyController.ManageAction));
        await h.Controller.ManageAsync(KeyController.Key.From(Appear("m", 0, 0, action: KeyController.ManageAction)));

        Assert.Equal("manage", FindIcon(h, "m"));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task DeckSeenReportsNameAndSize()
    {
        using var h = new Harness();
        var seen = new List<ManagedLauncher.Core.Config.KnownDeck>();
        h.Controller.DeckSeen = seen.Add;

        await h.HandleAsync(Connect());

        Assert.Equal([new ManagedLauncher.Core.Config.KnownDeck(Device, "Desk", 5, 3)], seen);
    }

    [Fact]
    public async Task LauncherEnteredDirectlyHasNoBackKey()
    {
        using var h = new Harness();
        for (var i = 0; i < 12; i++)
            h.AddApp($"app{i:00}");

        await h.HandleAsync(Appear("k10", 0, 2));

        Assert.EndsWith("app10.exe", FindIcon(h, "k10"));
    }

    [Fact]
    public async Task ManageKeyOpensManager()
    {
        using var h = new Harness();
        h.AddApp("a");
        var opened = 0;
        h.Controller.OpenManager = () => ++opened > 0;

        await h.HandleAsync(Appear("manage", 4, 2));
        Assert.Equal("manage", FindIcon(h, "manage"));

        await h.Controller.ManageAsync(KeyController.Key.From(Appear("manage", 4, 2)));
        Assert.Equal(1, opened);
        Assert.Contains(("ok", "manage", null), h.Deck.Calls);

        h.Controller.OpenManager = () => false;
        await h.Controller.ManageAsync(KeyController.Key.From(Appear("manage", 4, 2)));
        Assert.Contains(("alert", "manage", null), h.Deck.Calls);
    }

    [Fact]
    public async Task ManageMovesLeftOfNextPageOnLastPage()
    {
        using var h = new Harness();
        for (var i = 0; i < 16; i++)
            h.AddApp($"app{i:00}");
        await h.HandleAsync(Appear("k13", 3, 2));

        Assert.EndsWith("app13.exe", FindIcon(h, "k13"));

        await h.Controller.TurnPageAsync(KeyController.Key.From(Appear("nav", 4, 2)), +1);
        Assert.Equal("manage", FindIcon(h, "k13"));
    }

    [Fact]
    public async Task FixedSlotIgnoresPaging()
    {
        using var h = new Harness();
        for (var i = 0; i < 20; i++)
            h.AddApp($"app{i:00}");

        await h.HandleAsync(Appear("ctx", 4, 2, settings: """{"slot":17}"""));

        Assert.EndsWith("app17.exe", FindIcon(h, "ctx"));
    }

    [Fact]
    public async Task EmptyKeyIsBlankAndIgnoresPresses()
    {
        using var h = new Harness();
        await h.HandleAsync(Appear("ctx", 0, 0));

        await h.HandleAsync(Event("keyDown", "ctx", KeyController.SlotAction));
        await h.HandleAsync(Event("keyUp", "ctx", KeyController.SlotAction));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(("image", "ctx", "blank"), h.Deck.Calls);
        Assert.DoesNotContain(h.Deck.Calls, c => c.Item1 is "alert" or "ok");
    }

    [Fact]
    public async Task LongPressAcknowledgesBeforeClosing()
    {
        using var h = new Harness();
        var exe = h.AddApp("a");
        h.Source.Processes = [new ProcessInfo(42, exe, null, DateTime.UtcNow)];
        h.Operations.OnClose = () => h.Deck.Mark("closing");

        await h.Controller.ExecuteAsync(KeyController.Key.From(Appear("ctx", 0, 0)), isLong: true);

        var calls = h.Deck.Calls.Select(c => c.Item1).ToList();
        Assert.True(calls.IndexOf("ok") < calls.IndexOf("closing"));
        Assert.Single(calls, "ok");
    }

    [Fact]
    public async Task ReturnAfterLaunchGoesBackWhenOpenedFromProfile()
    {
        using var h = new Harness();
        h.Config.EnsureList("dev").Add(new Entry { Target = h.Tmp.Touch("tool.exe") });
        h.Config.ListOptions["dev"] = new ListOptions { ReturnAfterLaunch = true };
        await h.Controller.OpenLauncherAsync(KeyController.Key.From(
            Appear("opener", 0, 0, settings: """{"list":"dev"}""", action: KeyController.OpenAction)));
        await h.HandleAsync(Appear("k1", 1, 0));

        await h.Controller.ExecuteAsync(KeyController.Key.From(Appear("k1", 1, 0)), isLong: false);

        Assert.Equal(1, h.Operations.Launches);
        Assert.Contains(("switch", Device, null), h.Deck.Calls);
    }

    [Fact]
    public async Task ReturnAfterLaunchResetsPageWhenEnteredDirectly()
    {
        using var h = new Harness();
        for (var i = 0; i < 20; i++)
            h.AddApp($"app{i:00}");
        h.Config.ListOptions["default"] = new ListOptions { ReturnAfterLaunch = true };
        var nav = KeyController.Key.From(Appear("nav", 4, 2));
        await h.HandleAsync(Appear("nav", 4, 2));
        await h.Controller.TurnPageAsync(nav, +1);
        Assert.Equal(1, h.Controller.PageOf(Device));

        await h.Controller.ExecuteAsync(KeyController.Key.From(Appear("k0", 0, 0)), isLong: false);

        Assert.Equal(0, h.Controller.PageOf(Device));
        Assert.DoesNotContain(h.Deck.Calls, c => c.Item1 == "switch");
    }

    [Fact]
    public async Task NoReturnWhenOptionOffOrOnClose()
    {
        using var h = new Harness();
        var exe = h.AddApp("a");
        await h.Controller.OpenLauncherAsync(KeyController.Key.From(Appear("opener", 0, 0, action: KeyController.OpenAction)));

        await h.Controller.ExecuteAsync(KeyController.Key.From(Appear("k1", 1, 0)), isLong: false);
        Assert.DoesNotContain(h.Deck.Calls, c => c == ("switch", Device, null));

        h.Config.ListOptions["default"] = new ListOptions { ReturnAfterLaunch = true };
        h.Source.Processes = [new ProcessInfo(42, exe, null, DateTime.UtcNow.AddMinutes(-1))];
        await h.Controller.ExecuteAsync(KeyController.Key.From(Appear("k1", 1, 0)), isLong: true);
        Assert.DoesNotContain(h.Deck.Calls, c => c == ("switch", Device, null));
    }

    [Fact]
    public async Task LaunchedAppStaysStartingUntilItShowsAWindow()
    {
        using var h = new Harness();
        var exe = h.AddApp("slow");
        var hasWindow = false;
        h.Controller.HasWindow = _ => hasWindow;
        var key = KeyController.Key.From(Appear("k0", 0, 0));
        await h.HandleAsync(Appear("k0", 0, 0));

        await h.Controller.ExecuteAsync(key, isLong: false);
        Assert.Equal(RunState.Starting, h.Controller.StateOf(key, h.Tracker.Refresh()));
        Assert.Contains(h.Images.Visuals, v => v.State == RunState.Starting);

        h.Source.Processes = [new ProcessInfo(7, exe, null, DateTime.UtcNow)];
        Assert.Equal(RunState.Starting, h.Controller.StateOf(key, h.Tracker.Refresh()));

        hasWindow = true;
        Assert.Equal(RunState.Running, h.Controller.StateOf(key, h.Tracker.Refresh()));
        hasWindow = false;
        Assert.Equal(RunState.Running, h.Controller.StateOf(key, h.Tracker.Refresh()));
    }

    [Fact]
    public async Task ProcessesStartedByALaunchStubCountAsTheApp()
    {
        using var h = new Harness();
        h.AddApp("shim");
        var windows = new HashSet<int>();
        h.Controller.HasWindow = pids => pids.Any(windows.Contains);
        h.Operations.StartedPid = 100;
        var key = KeyController.Key.From(Appear("k0", 0, 0));
        await h.HandleAsync(Appear("k0", 0, 0));

        await h.Controller.ExecuteAsync(key, isLong: false);
        var now = DateTime.UtcNow;
        h.Source.Processes =
        [
            new ProcessInfo(101, @"C:\pkg\tools\real.exe", null, now, ParentPid: 100),
            new ProcessInfo(102, @"C:\Temp\real64.exe", null, now, Elevated: true, ParentPid: 101),
            new ProcessInfo(103, @"C:\other.exe", null, now.AddHours(-1), ParentPid: 101),
        ];
        Assert.Equal(RunState.Starting, h.Controller.StateOf(key, h.Tracker.Refresh()));

        windows.Add(102);
        Assert.Equal(RunState.Elevated, h.Controller.StateOf(key, h.Tracker.Refresh()));

        h.Operations.CanFocus = true;
        await h.Controller.ExecuteAsync(key, isLong: false);
        Assert.Equal(1, h.Operations.Launches);
    }

    [Fact]
    public void ElevatedProcessShowsElevated()
    {
        using var h = new Harness();
        var exe = h.AddApp("admin");
        h.Source.Processes = [new ProcessInfo(9, exe, null, DateTime.UtcNow.AddMinutes(-1), Elevated: true)];

        Assert.Equal(RunState.Elevated, h.Controller.StateOf(KeyController.Key.From(Appear("k0", 0, 0)), h.Tracker.Refresh()));
    }

    [Fact]
    public async Task RepeatedPressesDuringStartupLaunchOnce()
    {
        using var h = new Harness();
        var exe = h.AddApp("a");
        h.Operations.CanFocus = false;
        var key = KeyController.Key.From(Appear("ctx", 0, 0));

        await h.Controller.ExecuteAsync(key, isLong: false);
        h.Source.Processes = [new ProcessInfo(1, exe, null, DateTime.UtcNow)];
        await h.Controller.ExecuteAsync(key, isLong: false);
        await h.Controller.ExecuteAsync(key, isLong: false);

        Assert.Equal(1, h.Operations.Launches);
    }

    [Fact]
    public async Task UnchangedStateDoesNotRedraw()
    {
        using var h = new Harness();
        h.AddApp("a");
        await h.HandleAsync(Appear("ctx", 0, 0));
        var renders = h.Images.Renders;

        h.Tracker.Refresh();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(renders, h.Images.Renders);
    }

    [Fact]
    public async Task ConfigChangeRedrawsEveryKey()
    {
        using var h = new Harness();
        h.AddApp("a");
        await h.HandleAsync(Appear("a", 0, 0));
        await h.HandleAsync(Appear("b", 1, 0));

        h.Config = new AppConfig().Normalize();
        h.Config.EnsureList(null).Add(new Entry { Target = h.Tmp.Touch("x.exe") });
        h.Config.EnsureList(null).Add(new Entry { Target = h.Tmp.Touch("y.exe") });
        await h.Controller.OnConfigChangedAsync();

        Assert.EndsWith("y.exe", FindIcon(h, "b"));
    }

    [Fact]
    public async Task LaunchActionReadsEntryFromSettings()
    {
        using var h = new Harness();
        var exe = h.Tmp.Touch("Inline.exe").Replace("\\", "\\\\");

        await h.HandleAsync(Appear("ctx", 0, 0, settings: $$"""{"target":"{{exe}}","onRunning":"relaunch"}""", action: KeyController.LaunchAction));

        Assert.EndsWith("Inline.exe", FindIcon(h, "ctx"));
    }

    [Fact]
    public async Task BrokenTargetRendersBrokenVisual()
    {
        using var h = new Harness();
        h.Config.EnsureList(null).Add(new Entry { Target = @"C:\Users\steph\AppData\Local\Vivaldi\Application\vivaldi.exe" });

        await h.HandleAsync(Appear("ctx", 0, 0));

        Assert.True(h.Images.LastVisual!.Broken);
    }

    [Fact]
    public void WatcherKeepsLastGoodConfigOnInvalidJson()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Save(path, new AppConfig { LongPressMs = 1234 });
        using var watcher = new ConfigWatcher(path);
        var failures = new List<string>();
        watcher.LoadFailed += failures.Add;

        File.WriteAllText(path, "{ not json");
        watcher.Reload();

        Assert.Equal(1234, watcher.Current.LongPressMs);
        Assert.Single(failures);
    }

    [Fact]
    public void WatcherDebouncesBurstIntoOneReload()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        var time = new FakeTimeProvider();
        using var watcher = new ConfigWatcher(path, time, TimeSpan.FromMilliseconds(300));
        var reloads = 0;
        watcher.Changed += _ => reloads++;

        ConfigStore.Save(path, new AppConfig { LongPressMs = 1 });
        watcher.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(100));
        ConfigStore.Save(path, new AppConfig { LongPressMs = 2 });
        watcher.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, reloads);
        Assert.Equal(2, watcher.Current.LongPressMs);
    }

    internal sealed class FakeSource : IProcessSource
    {
        public IReadOnlyList<ProcessInfo> Processes { get; set; } = [];

        public IReadOnlyList<ProcessInfo> Snapshot() => Processes;
    }

    internal sealed class FakeOperations : IAppOperations
    {
        public Action? OnClose { get; set; }
        public int Launches { get; private set; }
        public bool CanFocus { get; set; } = true;

        /// <summary>Pid reported for the process each launch starts; null like a shell launch that reports none.</summary>
        public int? StartedPid { get; set; }

        public OperationResult Launch(ResolvedTarget target, LaunchOptions options, bool elevated)
        {
            Launches++;
            return OperationResult.Started(StartedPid);
        }

        public bool TryFocus(IReadOnlyList<ProcessInfo> processes) => CanFocus;

        public Task<OperationResult> CloseAsync(IReadOnlyList<ProcessInfo> processes, TimeSpan timeout)
        {
            OnClose?.Invoke();
            return Task.FromResult(OperationResult.Ok);
        }
    }

    internal sealed class FakeImages : IKeyImages
    {
        private readonly List<KeyVisual> visuals = [];

        public int Renders { get { lock (visuals) return visuals.Count; } }
        public KeyVisual? LastVisual { get { lock (visuals) return visuals.LastOrDefault(); } }
        public IReadOnlyList<KeyVisual> Visuals { get { lock (visuals) return visuals.ToList(); } }

        /// <summary>Image strings carry the icon source so tests can tell which app a key shows.</summary>
        public Task<string> RenderAsync(KeyVisual visual, string? customIcon)
        {
            lock (visuals) visuals.Add(visual);
            return Task.FromResult("icon:" + visual.IconSource);
        }

        public string NextPage(int page, int pages) => $"next {page + 1}/{pages}";

        public string Blank => "blank";

        public string Back => "back";

        public string Manage => "manage";
        public string Open(string list) => $"open {list}";

    }

    internal sealed class FakeDeck : IStreamDeck
    {
        private readonly List<(string, string, string?)> calls = [];

        public IReadOnlyList<(string, string, string?)> Calls
        {
            get { lock (calls) return calls.ToList(); }
        }

        public void Mark(string what) => Add(what, "", null);

        private Task Add(string what, string context, string? value)
        {
            lock (calls) calls.Add((what, context, value));
            return Task.CompletedTask;
        }

        public string? LastImage(string context)
        {
            lock (calls)
                return calls.LastOrDefault(c => c.Item1 == "image" && c.Item2 == context).Item3;
        }

        public Task SetTitleAsync(string context, string? title) => Add("title", context, title);
        public Task SetImageAsync(string context, string? pngBase64) => Add("image", context, pngBase64);
        public Task ShowOkAsync(string context) => Add("ok", context, null);
        public Task ShowAlertAsync(string context) => Add("alert", context, null);
        public Task SetSettingsAsync(string context, JsonNode settings) => Add("settings", context, settings.ToJsonString());
        public Task SendToPropertyInspectorAsync(string action, string context, JsonNode payload) => Add("pi", context, payload.ToJsonString());
        public Task LogAsync(string message) => Task.CompletedTask;
        public List<string> SwitchContexts { get; } = [];

        public Task SwitchToProfileAsync(string context, string device, string? profile)
        {
            lock (calls) SwitchContexts.Add(context);
            return Add("switch", device, profile);
        }
    }
}
