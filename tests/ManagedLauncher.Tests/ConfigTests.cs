using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Tests;

public class ConfigTests
{
    [Fact]
    public void MissingFileYieldsDefaults()
    {
        using var tmp = new TempDir();

        var config = ConfigStore.Load(tmp.File("config.json"));

        Assert.Equal(2000, config.LongPressMs);
        Assert.Equal(1500, config.PollMs);
        Assert.Equal(2000, config.CloseTimeoutMs);
        Assert.Empty(config.GetList(null));
    }

    [Fact]
    public void SaveThenLoadRoundTripsWithCamelCaseEnums()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        var config = new AppConfig();
        config.Insert(null, new Entry { Target = @"C:\a.exe" });
        config.Insert(null, new Entry { Target = @"C:\x.exe", Kind = TargetKind.Exe, OnRunning = OnRunning.Relaunch });

        ConfigStore.Save(path, config);
        var json = File.ReadAllText(path);
        var loaded = ConfigStore.Load(path);

        Assert.Contains("\"onRunning\": \"relaunch\"", json);
        Assert.Equal(2, loaded.GetList(null).Count);
        Assert.Equal(TargetKind.Exe, loaded.GetList(null)[1].Kind);
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
    }

    [Fact]
    public void MalformedJsonFailsTryLoad()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        File.WriteAllText(path, "{ \"slots\": [ { \"target\": ");

        Assert.False(ConfigStore.TryLoad(path, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SaveReplacesExistingFile()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Save(path, new AppConfig { LongPressMs = 1 });

        ConfigStore.Save(path, new AppConfig { LongPressMs = 2 });

        Assert.Equal(2, ConfigStore.Load(path).LongPressMs);
    }

    [Fact]
    public void UnsetSettingsUseBuiltInDefaults()
    {
        var options = LaunchOptions.Resolve(new Entry { Args = "--x", Admin = true });

        Assert.Equal("--x", options.Args);
        Assert.True(options.Admin);
        Assert.Equal(KillMode.All, options.KillMode);
        Assert.Equal(OnRunning.Auto, options.OnRunning);
        Assert.Equal(LongPressMode.Auto, options.LongPress);
        Assert.Equal(IndicatorStyle.Dot, options.Indicator);
    }

    [Fact]
    public void LegacyPresetsAreFoldedIntoEntriesAndNotWrittenBack()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        File.WriteAllText(path, """
            {
              "presets": {
                "default": { "args": "--d", "killMode": "newest" },
                "tray": { "args": "--t", "onRunning": "relaunch" }
              },
              "lists": { "default": [
                { "target": "a.exe", "preset": "tray", "args": "--own" },
                { "target": "b.exe", "preset": "tray" },
                { "target": "c.exe" },
                { "target": "d.exe", "preset": "gone" }
              ] }
            }
            """);

        var config = ConfigStore.Update(path, _ => { });
        var apps = config.GetList(null);
        var json = File.ReadAllText(path);

        Assert.Equal(("--own", OnRunning.Relaunch, KillMode.Newest), (apps[0].Args, apps[0].OnRunning, apps[0].KillMode));
        Assert.Equal(("--t", OnRunning.Relaunch), (apps[1].Args, apps[1].OnRunning));
        Assert.Equal(("--d", KillMode.Newest, (OnRunning?)null), (apps[2].Args, apps[2].KillMode, apps[2].OnRunning));
        Assert.Equal("--d", apps[3].Args);
        Assert.DoesNotContain("preset", json);
    }

    [Fact]
    public void InsertRemoveMoveKeepListGapFree()
    {
        var config = new AppConfig();
        config.Insert(null, new Entry { Target = "a" });
        config.Insert(null, new Entry { Target = "c" });
        Assert.Equal(1, config.Insert(null, new Entry { Target = "b" }, at: 1));

        Assert.True(config.Move(null, 2, 0));
        Assert.Equal(["c", "a", "b"], config.GetList(null).Select(a => a.Target));
        Assert.True(config.Remove(null, 1));
        Assert.Equal(["c", "b"], config.GetList(null).Select(a => a.Target));
        Assert.False(config.Remove(null, 5));
        Assert.False(config.Move(null, 0, 9));
    }

    [Fact]
    public void LegacyAppsMoveIntoDefaultListAndAreNotWrittenBack()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        File.WriteAllText(path, """{ "apps": [ { "target": "a.exe" } ], "lists": { "dev": [ { "target": "b.exe" } ] } }""");

        var config = ConfigStore.Load(path);
        ConfigStore.Save(path, config);

        Assert.Equal(["a.exe"], config.GetList("default").Select(a => a.Target));
        Assert.Equal(["b.exe"], config.GetList("DEV").Select(a => a.Target));
        Assert.DoesNotContain("\"apps\"", File.ReadAllText(path));
    }

    [Fact]
    public void NormalizeDropsNullAndBlankEntries()
    {
        var config = new AppConfig { Apps = [new Entry { Target = "a" }, null!, new Entry { Target = " " }] }.Normalize();

        Assert.Equal(["a"], config.GetList(null).Select(a => a.Target));
    }

    [Fact]
    public void CopyListDeepCopiesEntries()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("work").Add(new Entry { Target = "a.exe", Title = "A" });

        var name = config.CopyList("work", "home");
        config.EnsureList("home")[0].Title = "changed";
        config.EnsureList("home").Add(new Entry { Target = "b.exe" });

        Assert.Equal("home", name);
        Assert.Equal("A", config.GetList("work")[0].Title);
        Assert.Single(config.GetList("work"));
        Assert.Equal(2, config.GetList("home").Count);
    }

    [Fact]
    public void CopyListPicksFreeNameWhenNoneOrTaken()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("work");

        Assert.Equal("work copy", config.CopyList("work"));
        Assert.Equal("work copy 2", config.CopyList("work", " "));
        Assert.Equal("work copy 3", config.CopyList("work", "WORK"));
    }

    [Fact]
    public void DeckMappingToMissingListIsDropped()
    {
        var config = new AppConfig { Decks = new() { ["d1"] = "work", ["d2"] = "gone" } };
        config.EnsureList("work");

        config.Normalize();

        Assert.Equal("work", config.Decks["d1"]);
        Assert.False(config.Decks.ContainsKey("d2"));
    }

    [Fact]
    public void KnownDecksUpsertReplacesByIdAndSkipsUnchanged()
    {
        using var tmp = new TempDir();
        var path = tmp.File("devices.json");

        Assert.True(KnownDecks.Upsert(path, new KnownDeck("a", "Stream Deck", 5, 3)));
        Assert.True(KnownDecks.Upsert(path, new KnownDeck("b", "XL", 8, 4)));
        Assert.False(KnownDecks.Upsert(path, new KnownDeck("a", "Stream Deck", 5, 3)));
        Assert.True(KnownDecks.Upsert(path, new KnownDeck("a", "Desk", 5, 3)));

        Assert.Equal([new KnownDeck("a", "Desk", 5, 3), new KnownDeck("b", "XL", 8, 4)], KnownDecks.Load(path));
    }

    [Fact]
    public void KnownDecksMissingOrBrokenFileIsEmpty()
    {
        using var tmp = new TempDir();
        var path = tmp.File("devices.json");
        Assert.Empty(KnownDecks.Load(path));
        File.WriteAllText(path, "{ not json");
        Assert.Empty(KnownDecks.Load(path));
    }

    [Fact]
    public void RenameMovesEntriesDecksAndKeepsOldNameResolving()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("work").Add(new Entry { Target = @"C:.exe" });
        config.Decks["d1"] = "work";

        Assert.Null(config.RenameList("work", "Office"));
        Assert.Null(config.RenameList("Office", "Desk"));

        Assert.False(config.Lists.ContainsKey("work"));
        Assert.Single(config.GetList("Desk"));
        Assert.Equal("Desk", config.ResolveList("work"));
        Assert.Equal("Desk", config.ResolveList("office"));
        Assert.Single(config.GetList("work"));
        Assert.Equal("Desk", config.Decks["d1"]);
    }

    [Fact]
    public void RenameRefusesTakenOrEmptyName()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("a");
        config.EnsureList("b");

        Assert.NotNull(config.RenameList("a", "B"));
        Assert.NotNull(config.RenameList("a", "  "));
        Assert.NotNull(config.RenameList("missing", "x"));
        Assert.True(config.Lists.ContainsKey("a"));
    }

    [Fact]
    public void RenameCaseOnlyKeepsNoAlias()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("games");

        Assert.Null(config.RenameList("games", "Games"));

        Assert.Contains("Games", config.Lists.Keys);
        Assert.Empty(config.Renames);
    }

    [Fact]
    public void NewListWithOldNameDropsAliasOnSave()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Update(path, c => { c.EnsureList("work"); c.RenameList("work", "office"); });

        Assert.Equal("office", ConfigStore.Load(path).ResolveList("work"));
        var config = ConfigStore.Update(path, c => Assert.True(c.AddList("work")));

        Assert.Empty(config.Renames);
        Assert.Equal("work", config.ResolveList("work"));
    }

    [Fact]
    public void RenamedDefaultStaysHomeAndIsNotRecreated()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Update(path, c => c.Insert(null, new Entry { Target = @"C:.exe" }));

        var config = ConfigStore.Update(path, c => Assert.Null(c.RenameList("default", "Main")));
        config = ConfigStore.Update(path, c => c.Insert(null, new Entry { Target = @"C:.exe" }));
        config = ConfigStore.Load(path);

        Assert.Equal(["Main"], config.Lists.Keys);
        Assert.Equal("Main", config.HomeList);
        Assert.Equal(2, config.GetList(null).Count);
    }

    [Fact]
    public void UnusedListNameCounts()
    {
        var config = new AppConfig().Normalize();
        Assert.Equal("New list", config.UnusedListName("New list"));
        config.EnsureList("New list");
        config.EnsureList("New list 2");
        Assert.Equal("New list 3", config.UnusedListName("New list"));
    }

    [Fact]
    public void ListOptionsFollowRenameAndCopyAndDropWithList()
    {
        var config = new AppConfig().Normalize();
        config.EnsureList("work");
        config.ListOptions["work"] = new ListOptions { ReturnAfterLaunch = true };

        config.RenameList("work", "office");
        var copy = config.CopyList("office");
        config.ListOptions["office"].ReturnAfterLaunch = false;

        Assert.True(config.OptionsOf("work") is { ReturnAfterLaunch: false });
        Assert.True(config.OptionsOf(copy).ReturnAfterLaunch);
        Assert.False(config.OptionsOf("missing").ReturnAfterLaunch);

        config.Lists.Remove(copy);
        config.Normalize();
        Assert.False(config.ListOptions.ContainsKey(copy));
    }

    [Fact]
    public async Task ConcurrentUpdatesAllLand()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Save(path, new AppConfig());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(
            () => ConfigStore.Update(path, c => c.Insert(null, new Entry { Target = $@"C:pp{i}.exe" })),
            TestContext.Current.CancellationToken)));

        Assert.Equal(20, ConfigStore.Load(path).GetList(null).Count);
    }

    [Fact]
    public void FailedMutationLeavesFileUntouched()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Update(path, c => c.Insert(null, new Entry { Target = @"C:.exe" }));
        var before = File.ReadAllText(path);

        Assert.Throws<InvalidOperationException>(() => ConfigStore.Update(path, c =>
        {
            c.Lists.Clear();
            throw new InvalidOperationException();
        }));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void ListsDifferingOnlyByCaseAreBothKept()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        File.WriteAllText(path, """{"lists":{"Work":[{"target":"a.exe"}],"work":[{"target":"b.exe"}]},"presets":{"x":{},"X":{}}}""");

        Assert.True(ConfigStore.TryLoad(path, out var config, out var error), error);

        Assert.Single(config.GetList("Work"));
        Assert.Single(config.GetList("work 2"));
    }

    [Fact]
    public void BreaksSurviveSaveAndAreNotApps()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        ConfigStore.Update(path, c =>
        {
            c.Insert(null, new Entry { Target = "a.exe" });
            c.Insert(null, new Entry { Break = ListBreak.Page });
            c.Insert(null, new Entry { Target = "b.exe" });
        });

        var config = ConfigStore.Load(path);

        Assert.Equal(3, config.GetList(null).Count);
        Assert.Null(config.GetApp(null, 1));
        Assert.Equal([Core.Paging.ListItemKind.App, Core.Paging.ListItemKind.PageBreak, Core.Paging.ListItemKind.App], config.LayoutOf(null));
        Assert.Contains("\"break\": \"page\"", File.ReadAllText(path));
    }
}
