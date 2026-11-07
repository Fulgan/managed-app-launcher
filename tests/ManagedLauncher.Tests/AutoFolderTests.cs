using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Tests;

public class AutoFolderTests
{
    internal sealed class FakeSources : IAppSources
    {
        public List<SteamGame> Games { get; } = [];
        public List<string> Roots { get; } = [];
        public List<(string Name, string Aumid)> Packaged { get; } = [];
        public List<AppChoice> RunningApps { get; } = [];

        public IReadOnlyList<SteamGame> SteamGames() => Games;
        public IReadOnlyList<string> StartMenuRoots() => Roots;
        public IReadOnlyList<(string Name, string Aumid)> PackagedApps() => Packaged;
        public IReadOnlyList<AppChoice> Running() => RunningApps;
    }

    private static (AppConfig Config, string Id) Auto(ListSource source, string? parent = null)
    {
        var config = new AppConfig().Normalize();
        Assert.Null(config.AddAutoFolder(parent, "Auto", source, out var id, out _));
        return (config, id);
    }

    [Fact]
    public void DirectoryListsSubdirectoriesFirstThenFilesOfChosenTypes()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.File("Tools"));
        tmp.Touch(@"apps\deep\run.bat");
        Directory.CreateDirectory(tmp.File("empty"));
        tmp.Touch(@"docs\readme.txt");
        tmp.Touch("b.exe");
        tmp.Touch("A.cmd");
        tmp.Touch("c.ps1");
        tmp.Touch("readme.txt");
        tmp.Touch(@"Tools\inner.exe");
        var (config, id) = Auto(new ListSource { Type = SourceType.Directory, Path = tmp.Path });

        var items = config.Generate(id, new FakeSources());

        Assert.Equal(["apps", "Tools"], items.Take(2).Select(i => i.Title));
        Assert.Equal(["A.cmd", "b.exe", "c.ps1"], items.Skip(2).Select(i => Path.GetFileName(i.Target)));
        Assert.True(items[1].IsFolder);
        var tools = items[1].Folder!;
        Assert.Equal(AutoFolders.NestedId(id, "Tools"), tools);
        Assert.Equal("Tools", config.NameOf(tools));
        Assert.Equal("default/Auto/Tools", config.PathOf(tools));
        Assert.True(config.Exists(tools));
        Assert.Equal(3, config.DepthOf(tools));
        Assert.EndsWith("inner.exe", Assert.Single(config.Generate(tools, new FakeSources())).Target);

        config.Store[id].Source!.Types = ["exe"];
        Assert.Equal(["b.exe"], config.Generate(id, new FakeSources()).Where(i => i.IsApp).Select(i => Path.GetFileName(i.Target)));
    }

    [Fact]
    public void HiddenTargetsAndDirectoriesAreLeftOutAndOverridesWin()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.File("Skip"));
        var a = tmp.Touch("a.exe");
        var b = tmp.Touch("b.exe");
        var (config, id) = Auto(new ListSource { Type = SourceType.Directory, Path = tmp.Path });
        config.Store[id].Hidden = [b.ToUpperInvariant(), tmp.File("Skip")];
        config.Store[id].Items.Add(new Entry { Target = a, Title = "Alpha", Admin = true });

        var item = Assert.Single(config.Generate(id, new FakeSources()));

        Assert.Equal("Alpha", item.Title);
        Assert.True(item.Admin);
        Assert.Equal(TargetKind.Exe, item.Kind);
    }

    [Fact]
    public void NestedFoldersStopAtDepthLimit()
    {
        using var tmp = new TempDir();
        tmp.Touch(@"one\two\three\x.exe");
        tmp.Touch(@"one\two\y.exe");
        var (config, id) = Auto(new ListSource { Type = SourceType.Directory, Path = tmp.Path });
        var sources = new FakeSources();

        var one = config.Generate(id, sources).Single().Folder!;
        var two = config.Generate(one, sources).Single().Folder!;

        Assert.Equal(4, config.DepthOf(two));
        Assert.EndsWith("y.exe", Assert.Single(config.Generate(two, sources)).Target);
    }

    [Fact]
    public void StartMenuMergesRootsKeepsFoldersAndAddsStoreAppsAtTop()
    {
        using var common = new TempDir();
        using var user = new TempDir();
        common.Touch("Paint.lnk");
        common.Touch(@"Office\Word.lnk");
        user.Touch("paint.lnk");
        user.Touch(@"office\Excel.lnk");
        user.Touch(@"Office\Uninstall Office.lnk");
        Directory.CreateDirectory(user.File("Empty"));
        var sources = new FakeSources { Roots = { common.Path, user.Path }, Packaged = { ("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") } };
        var (config, id) = Auto(new ListSource { Type = SourceType.StartMenu });

        var top = config.Generate(id, sources);

        Assert.Equal(["Office", "Calculator", "Paint"], top.Select(config.TitleOf));
        var office = config.Generate(top[0].Folder, sources);
        Assert.Equal(["Excel", "Word"], office.Select(config.TitleOf));
    }

    [Fact]
    public void SteamAndRunningAreFlatAndSorted()
    {
        var sources = new FakeSources
        {
            Games = { new SteamGame(2, "Zeta", @"C:\g\z", null), new SteamGame(1, "alpha", @"C:\g\a", null) },
            RunningApps = { new AppChoice("Notepad", "", new PinAdvisor.PinPlan(new Entry { Target = @"C:\Windows\notepad.exe" }, [])) },
        };
        var (steam, steamId) = Auto(new ListSource { Type = SourceType.Steam });
        var (running, runningId) = Auto(new ListSource { Type = SourceType.Running });

        Assert.Equal(["alpha", "Zeta"], steam.Generate(steamId, sources).Select(e => e.Title));
        Assert.Equal("Notepad", Assert.Single(running.Generate(runningId, sources)).Title);
    }

    [Fact]
    public void AutoFolderRoundTripsAndMissingPathIsEmpty()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json");
        string id = "";
        ConfigStore.Update(path, c =>
        {
            Assert.Null(c.AddAutoFolder(null, "Games", new ListSource { Type = SourceType.Directory, Path = @"%TEMP%\does-not-exist", Types = ["exe", "bat"] }, out id, out _));
            c.Store[id].Hidden = ["x"];
        });

        var config = ConfigStore.Load(path);
        var json = File.ReadAllText(path);

        Assert.True(config.IsDynamic(id));
        Assert.Equal(SourceType.Directory, config.Store[id].Source!.Type);
        Assert.Equal(["exe", "bat"], config.Store[id].Source!.Types);
        Assert.Equal(["x"], config.Store[id].Hidden);
        Assert.Contains("\"kind\": \"dynamic\"", json);
        Assert.Empty(config.Generate(id, new FakeSources()));
        Assert.True(config.GetItem(null, 0)!.IsFolder);
    }

    [Fact]
    public void OverrideKeepsOnlyChangedFieldsAndRawPreviewIgnoresHideAndOverrides()
    {
        using var tmp = new TempDir();
        var a = tmp.Touch("a.exe");
        var (config, id) = Auto(new ListSource { Type = SourceType.Directory, Path = tmp.Path });
        var generated = config.Generate(id, new FakeSources()).Single();
        var edited = AutoFolders.Merge(generated, new Entry { Target = a, Title = "Alpha" });

        config.SetOverride(id, edited, generated);
        var stored = Assert.Single(config.Store[id].Items);
        Assert.Equal(("Alpha", (TargetKind?)null), (stored.Title, stored.Kind));

        config.SetHidden(id, a, true);
        Assert.True(config.IsHidden(id, a.ToUpperInvariant()));
        Assert.Empty(config.Generate(id, new FakeSources()));
        var raw = Assert.Single(config.Generate(id, new FakeSources(), raw: true));
        Assert.Null(raw.Title);

        config.SetOverride(id, generated, generated);
        config.SetHidden(id, a, false);
        Assert.Empty(config.Store[id].Items);
        Assert.Null(config.Normalize().Store[id].Hidden);
    }
}
