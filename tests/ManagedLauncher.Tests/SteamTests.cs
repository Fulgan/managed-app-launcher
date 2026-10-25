using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Tests;

public class SteamTests
{
    private static void Manifest(string library, int id, string name, string dir) =>
        File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"), $$"""
            "AppState"
            {
            	"appid"		"{{id}}"
            	"name"		"{{name}}"
            	"installdir"		"{{dir}}"
            }
            """);

    [Fact]
    public void FindsGamesInEveryLibraryAndSkipsSteamTools()
    {
        using var tmp = new TempDir();
        var steam = tmp.MakeDir("Steam");
        var games = tmp.MakeDir(@"Games\SteamLibrary");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(games, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0" { "path"		"{{steam.Replace(@"\", @"\\")}}" }
            	"1" { "path"		"{{games.Replace(@"\", @"\\")}}" }
            }
            """);
        Manifest(steam, 892970, "Valheim", "Valheim");
        Manifest(steam, 228980, "Steamworks Common Redistributables", "Steamworks Shared");
        Manifest(games, 1336490, "Against the Storm", "Against the Storm");
        Manifest(games, 1493710, "Proton Experimental", "Proton - Experimental");
        var icon = Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", "892970")).FullName;
        File.WriteAllText(Path.Combine(icon, "header.jpg"), "");
        File.WriteAllText(Path.Combine(icon, "4afa5b434598d9704ceb4701ceeb2c04981c5a37.jpg"), "");

        var found = SteamLibrary.Games(steam);

        Assert.Equal(["Against the Storm", "Valheim"], found.Select(g => g.Name));
        var valheim = found[1].ToEntry();
        Assert.Equal("steam://rungameid/892970", valheim.Target);
        Assert.Equal(TargetKind.Url, valheim.Kind);
        Assert.Equal(Path.Combine(steam, "steamapps", "common", "Valheim") + @"\", valheim.TrackPath);
        Assert.EndsWith("4afa5b434598d9704ceb4701ceeb2c04981c5a37.jpg", valheim.Icon);
        Assert.Null(found[0].Icon);
    }

    [Theory]
    [InlineData("steam://rungameid/570", TargetKind.Url)]
    [InlineData(@"C:\Games\x.exe", TargetKind.Exe)]
    [InlineData("com.squirrel.slack.slack", TargetKind.Aumid)]
    public void DetectsLinks(string target, TargetKind kind) => Assert.Equal(kind, TargetKindDetector.Detect(target));

    [Fact]
    public void GameRunsWhenAnyProcessRunsFromItsFolder()
    {
        var entry = new Entry { Target = "https://example.com/game", TrackPath = @"C:\Steam\steamapps\common\Valheim\" };
        var target = TargetResolver.Resolve(entry);
        var snapshot = new ProcessSnapshot([
            new ProcessInfo(1, @"C:\Steam\steamapps\common\Valheim\valheim.exe", null, DateTime.UtcNow),
            new ProcessInfo(2, @"C:\Steam\steamapps\common\ValheimServer\server.exe", null, DateTime.UtcNow),
        ]);

        Assert.True(target.IsLaunchable);
        Assert.Equal([1], snapshot.Find(target).Select(p => p.Pid));
    }

    [Fact]
    public void LinksCannotRunAsAdmin()
    {
        var target = TargetResolver.Resolve(new Entry { Target = "https://example.com/game" });

        Assert.Equal(Core.Press.PressDecision.Alert,
            Core.Press.ActionPlanner.Plan(false, false, LaunchOptions.Resolve(new Entry { Admin = true }), target));
        Assert.Equal("", Core.Press.Win32AppOperations.BuildStartInfo(target, LaunchOptions.Defaults, false).WorkingDirectory);
    }

    [Fact]
    public void LinkWithoutRegisteredHandlerIsBroken()
    {
        var missing = TargetResolver.Resolve(new Entry { Target = "nosuchscheme7f3a://x" });
        var web = TargetResolver.Resolve(new Entry { Target = "https://example.com" });

        Assert.Equal("no program handles nosuchscheme7f3a: links", missing.Error);
        Assert.True(web.IsLaunchable);
    }
}
