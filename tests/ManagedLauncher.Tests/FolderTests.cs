using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Tests;

public class FolderTests
{
    private static AppConfig Load(TempDir tmp, string json)
    {
        var path = tmp.File("config.json");
        File.WriteAllText(path, json);
        Assert.True(ConfigStore.TryLoad(path, out var config, out var error), error);
        return config;
    }

    private static string Folder(AppConfig config, string parent, string name)
    {
        Assert.Null(config.AddFolder(parent, name, out var id, out _));
        return id;
    }

    [Fact]
    public void LegacyListsMigrateToStoreKeepingOrderOptionsRenamesAndDecks()
    {
        using var tmp = new TempDir();
        var config = Load(tmp, """
            {
              "lists": { "Main": [ { "target": "a.exe" } ], "games": [ { "target": "g.exe" } ] },
              "listOptions": { "games": { "returnAfterLaunch": true } },
              "renames": { "default": "Main", "play": "games" },
              "decks": { "d1": "play" }
            }
            """);

        var games = config.ResolveList("games");
        Assert.Equal([config.HomeList, games], config.Top);
        Assert.Equal("Main", config.NameOf(null));
        Assert.Equal(["a.exe"], config.GetList(null).Select(e => e.Target));
        Assert.True(config.OptionsOf(games).ReturnAfterLaunch);
        Assert.Equal(games, config.ResolveList("play"));
        Assert.Equal(games, config.Decks["d1"]);

        var path = tmp.File("config.json");
        ConfigStore.Save(path, config);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(games, (string?)json["lists"]!["games"]);
        Assert.Equal("default", (string?)json["lists"]!["Main"]);
        Assert.Null(json["listOptions"]);
        Assert.Null(json["store"]![games]!["name"]);
        Assert.Equal("static", (string?)json["store"]![games]!["kind"]);
        Assert.Equal(games, ConfigStore.Load(path).ResolveList("play"));
    }

    [Fact]
    public void MigratedIdsAreTheSameOnEveryLoad()
    {
        using var tmp = new TempDir();
        const string json = """{ "lists": { "default": [], "Games": [], "Work": [] } }""";

        var first = Load(tmp, json);
        var second = Load(tmp, json);

        Assert.Equal(first.Top, second.Top);
        Assert.Equal(3, first.Top.Distinct().Count());
    }

    [Fact]
    public void FoldersResolveByPathAndIdAndRoundTrip()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        var games = "";
        var steam = "";
        ConfigStore.Update(path, c =>
        {
            games = c.AddList("Games")!;
            steam = Folder(c, games, "Steam");
            c.Insert(steam, new Entry { Target = "s.exe" });
        });

        var config = ConfigStore.Load(path);

        Assert.Equal(steam, config.ResolveList("games/STEAM"));
        Assert.Equal(steam, config.ResolveList(steam.ToUpperInvariant()));
        Assert.Equal("Games/Steam", config.PathOf(steam));
        Assert.Equal(games, config.ParentOf(steam));
        Assert.Equal(2, config.DepthOf(steam));
        Assert.True(config.GetItem(games, 0)!.IsFolder);
        Assert.Null(config.GetApp(games, 0));
        Assert.Equal("Steam", config.TitleOf(config.GetItem(games, 0)!));
        Assert.Equal([(config.HomeList, 1), (games, 1), (steam, 2)], config.Tree());
        Assert.Equal([Core.Paging.ListItemKind.App], config.LayoutOf(games));
    }

    [Fact]
    public void FolderNamesAreStoredWithTheListTopLevelNamesInLists()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        string steam = "";
        ConfigStore.Update(path, c =>
        {
            Assert.Null(c.RenameList(null, "Main"));
            Assert.Null(c.AddFolder(null, "Steam", out steam, out _));
            c.UpdateOptions(steam, o => o.ReturnAfterLaunch = true);
        });

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal("default", (string?)json["lists"]!["Main"]);
        Assert.Single(json["lists"]!.AsObject());
        Assert.Equal("Steam", (string?)json["store"]![steam]!["name"]);
        Assert.Null(json["store"]!["default"]!["name"]);
        Assert.Null(json["top"]);
        Assert.Equal("Main/Steam", ConfigStore.Load(path).PathOf(steam));
    }

    [Fact]
    public void DynamicKindAndSourceRoundTrip()
    {
        using var tmp = new TempDir();
        var config = Load(tmp, """
            {
              "lists": { "Home": "default", "Tools": "abcdef01" },
              "store": {
                "default": { "items": [] },
                "abcdef01": { "kind": "dynamic", "source": { "type": "directory", "path": "C:/Tools", "types": [ "exe" ] }, "items": [] }
              }
            }
            """);
        var path = tmp.File("config.json");
        ConfigStore.Save(path, config);
        var loaded = ConfigStore.Load(path);

        Assert.Equal(ListKind.Dynamic, loaded.Store["abcdef01"].Kind);
        Assert.True(loaded.Store["abcdef01"].Source is { Type: SourceType.Directory, Path: "C:/Tools" });
        Assert.Equal(["default", "abcdef01"], loaded.Top);
    }

    [Fact]
    public void SecondOwnerIsDroppedAndOrphanBecomesTopLevel()
    {
        using var tmp = new TempDir();
        var config = Load(tmp, """
            {
              "store": {
                "default": { "items": [ { "folder": "aaaaaaaa" }, { "folder": "aaaaaaaa" }, { "folder": "default" } ] },
                "aaaaaaaa": { "name": "A", "items": [ { "folder": "aaaaaaaa" } ] },
                "bbbbbbbb": { "name": "B", "items": [ { "folder": "cccccccc" }, { "link": "gone0000" } ] },
                "cccccccc": { "name": "C", "items": [ { "folder": "bbbbbbbb" } ] }
              },
              "lists": { "Home": "default" }
            }
            """);

        Assert.Single(config.GetList(null));
        Assert.Empty(config.GetList("aaaaaaaa"));
        Assert.Equal(["default", "bbbbbbbb"], config.Top);
        Assert.Equal("bbbbbbbb", config.ParentOf("cccccccc"));
        Assert.Empty(config.GetList("cccccccc"));
        Assert.Single(config.GetList("bbbbbbbb"));
    }

    [Fact]
    public void FolderNamesAreUniqueAmongSiblingsOnly()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var b = config.AddList("B")!;
        Folder(config, a, "Tools");

        Assert.NotNull(config.AddFolder(a, "tools", out _, out _));
        Assert.Null(config.AddFolder(b, "Tools", out _, out _));
        Assert.Null(config.AddFolder(a, "A", out _, out _));
        Assert.NotNull(config.RenameList(a, "b"));
    }

    [Fact]
    public void FolderDepthIsLimitedAndRaisablePerList()
    {
        var config = new AppConfig().Normalize();
        var level = config.HomeList;
        for (var depth = 2; depth <= AppConfig.DefaultMaxDepth; depth++)
            level = Folder(config, level, $"L{depth}");

        Assert.NotNull(config.AddFolder(level, "too deep", out _, out _));
        config.UpdateOptions(null, o => o.MaxDepth = 6);
        Assert.Null(config.AddFolder(level, "fits now", out _, out _));
        Assert.Equal(6, config.LimitOf(level));
    }

    [Fact]
    public void FolderCantMoveIntoItselfOrBeyondDepth()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var outer = Folder(config, a, "Outer");
        var inner = Folder(config, outer, "Inner");
        var deep = Folder(config, inner, "Deep");
        var b = config.AddList("B")!;
        var b2 = Folder(config, b, "B2");

        Assert.NotNull(config.MoveItem(a, 0, inner, copy: false));
        Assert.NotNull(config.CheckFolderPlacement(b2, outer));
        Assert.Null(config.CheckFolderPlacement(b, outer));
        Assert.NotNull(config.CheckFolderPlacement(config.HomeList, config.HomeList));

        Assert.Null(config.MoveItem(a, 0, b, copy: false));
        Assert.Equal(b, config.ParentOf(outer));
        Assert.Equal(4, config.DepthOf(deep));
        Assert.Empty(config.GetList(a));
    }

    [Fact]
    public void CopiedFolderIsDeepWithNewIds()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var tools = Folder(config, a, "Tools");
        var sub = Folder(config, tools, "Sub");
        config.Insert(sub, new Entry { Target = "x.exe" });
        var b = config.AddList("B")!;

        Assert.Null(config.MoveItem(a, 0, b, copy: true));

        var copy = config.Children(b).Single();
        Assert.NotEqual(tools, copy);
        var subCopy = config.Children(copy).Single();
        Assert.NotEqual(sub, subCopy);
        config.EnsureList(subCopy)[0].Target = "changed.exe";
        Assert.Equal("x.exe", config.GetList(sub)[0].Target);
        Assert.Equal(tools, config.Children(a).Single());

        var sibling = config.CopyList(tools);
        Assert.Equal("Tools copy", config.NameOf(sibling));
        Assert.Equal(a, config.ParentOf(sibling));
        Assert.Equal([tools, sibling], config.Children(a));
    }

    [Fact]
    public void RemovingFolderDropsSubtreeLinksAndDecks()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var tools = Folder(config, a, "Tools");
        var sub = Folder(config, tools, "Sub");
        config.Insert(sub, new Entry { Target = "x.exe" });
        Assert.Null(config.AddLink(null, sub, out _));
        config.Decks["d1"] = sub;

        Assert.Equal((1, 1), config.ContentsOf(tools));
        Assert.True(config.Remove(a, 0));

        Assert.False(config.Store.ContainsKey(tools));
        Assert.False(config.Store.ContainsKey(sub));
        Assert.Empty(config.GetList(null));
        Assert.Empty(config.Decks);
        Assert.False(config.RemoveList(null));
    }

    [Fact]
    public void LinkToOwnListOrAncestorOrLoopIsRefused()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var inner = Folder(config, a, "Inner");
        var b = config.AddList("B")!;

        Assert.NotNull(config.AddLink(a, a, out _));
        Assert.NotNull(config.AddLink(inner, a, out _));
        Assert.Null(config.AddLink(a, b, out _));
        Assert.NotNull(config.AddLink(b, a, out _));
        Assert.Null(config.AddLink(b, inner, out _));
        Assert.NotNull(config.AddLink(inner, b, out _));
        Assert.Null(config.AddLink(inner, config.HomeList, out _));
        Assert.NotNull(config.AddLink(config.HomeList, b, out _));
    }

    [Fact]
    public void LinkAtDepthLimitIsRefused()
    {
        var config = new AppConfig().Normalize();
        var level = config.HomeList;
        for (var depth = 2; depth <= AppConfig.DefaultMaxDepth; depth++)
            level = Folder(config, level, $"L{depth}");
        var b = config.AddList("B")!;

        Assert.NotNull(config.AddLink(level, b, out _));
    }

    [Fact]
    public void LoopingLinksOnLoadFlagOnlyTheFirstOfTheLoop()
    {
        using var tmp = new TempDir();
        var config = Load(tmp, """
            {
              "store": {
                "default": { "items": [ { "link": "bbbbbbbb", "title": "to B" } ] },
                "bbbbbbbb": { "items": [ { "link": "default" } ] }
              },
              "lists": { "Home": "default", "B": "bbbbbbbb" }
            }
            """);

        var first = config.GetItem(null, 0)!;
        var second = config.GetItem("bbbbbbbb", 0)!;
        Assert.True(first.IsLink);
        Assert.Equal("to B", config.TitleOf(first));
        Assert.Equal("Home", config.TitleOf(second));
        Assert.NotNull(first.LinkProblem);
        Assert.Null(second.LinkProblem);
    }

    [Fact]
    public void MovedLinkIsCheckedAgainstTargetList()
    {
        var config = new AppConfig().Normalize();
        var a = config.AddList("A")!;
        var b = config.AddList("B")!;
        Assert.Null(config.AddLink(a, b, out _));

        Assert.NotNull(config.MoveItem(a, 0, b, copy: false));
        Assert.Null(config.MoveItem(a, 0, config.HomeList, copy: true));
        Assert.Single(config.GetList(a));
        Assert.True(config.GetItem(null, 0)!.IsLink);
    }
}
