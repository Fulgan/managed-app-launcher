using System.IO.Compression;
using System.Text.Json;
using ManagedLauncher.Core;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Import;
using ManagedLauncher.Core.Targets;
using Microsoft.Win32;

namespace ManagedLauncher.Tests;

public class CliLogicTests
{
    [Fact]
    public void SuggestsGlobForVersionedFolder()
    {
        using var tmp = new TempDir();
        var exe = tmp.Touch("JetBrains", "IntelliJ IDEA 2024.3.1", "bin", "idea64.exe");

        var glob = PinAdvisor.SuggestGlob(exe);

        Assert.Equal(Path.Combine(tmp.Path, "JetBrains", "IntelliJ IDEA *", "bin", "idea64.exe"), glob);
    }

    [Fact]
    public void NoSuggestionForUnversionedPath()
    {
        using var tmp = new TempDir();

        Assert.Null(PinAdvisor.SuggestGlob(tmp.Touch("KeePass Password Safe 2", "KeePass.exe")));
    }

    [Fact]
    public void PinningVersionedShortcutKeepsShortcutAndAdvises()
    {
        using var tmp = new TempDir();
        var exe = tmp.Touch("Tool", "app-1.2.3", "tool.exe");
        var lnk = tmp.File("Tool.lnk");
        TargetTests.CreateShortcut(lnk, exe, "", tmp.Path);

        var plan = PinAdvisor.Plan(lnk, null);

        Assert.Equal(TargetKind.Lnk, plan.Entry.Kind);
        Assert.Equal(lnk, plan.Entry.Target);
        Assert.Contains(plan.Advice, a => a.Contains("app-*"));
    }

    [Fact]
    public void PinningSquirrelShortcutTracksRealApp()
    {
        using var tmp = new TempDir();
        var update = tmp.Touch("Discord", "Update.exe");
        tmp.Touch("Discord", "app-1.0.9", "Discord.exe");
        var lnk = tmp.File("Discord.lnk");
        TargetTests.CreateShortcut(lnk, update, "--processStart Discord.exe", tmp.Path);

        var plan = PinAdvisor.Plan(lnk, null);

        Assert.Equal(Path.Combine(tmp.Path, "Discord", "app-*", "Discord.exe"), plan.Entry.TrackPath);
    }

    [Theory]
    [InlineData("--processStart Discord.exe", "Discord.exe")]
    [InlineData(" --processStart \"GitHubDesktop.exe\"", "GitHubDesktop.exe")]
    public void ParsesSquirrelProcessStart(string args, string exe)
    {
        Assert.Equal(Path.Combine(@"C:\Apps\X", "app-*", exe), PinAdvisor.SquirrelAppPattern(@"C:\Apps\X\Update.exe", args));
        Assert.Null(PinAdvisor.SquirrelAppPattern(@"C:\Apps\X\Other.exe", args));
    }

    private static string Page(TempDir tmp, string actionsJson)
    {
        var path = tmp.File("page.json");
        File.WriteAllText(path, "{\"Controllers\":[{\"Type\":\"Keypad\",\"Actions\":{" + actionsJson + "}}]}");
        return path;
    }

    private static string Launcher(string exe, bool front, string args = "", bool admin = false) =>
        $$$"""
        {"UUID":"com.barraider.advancedlauncher","States":[{"Title":""}],
         "Settings":{"application":"{{{exe.Replace("\\", "/")}}}","appArguments":"{{{args}}}","appStartIn":"","bringToFront":{{{(front ? "true" : "false")}}},"runAsAdmin":{{{(admin ? "true" : "false")}}}}}
        """;

    [Fact]
    public void ImportMapsLauncherButtonsInReadingOrder()
    {
        using var tmp = new TempDir();
        var a = tmp.Touch("a.exe");
        var b = tmp.Touch("b.exe");
        var page = Page(tmp, $$$"""
            "0,0": {{{Launcher(a, front: true, admin: true)}}},
            "2,1": {{{Launcher(b, front: false)}}},
            "4,2": {"UUID":"com.elgato.streamdeck.page.next","Settings":{}}
            """);

        var keys = ProfileImporter.ReadPage(page, 1, () => []);

        var first = keys.Single(k => k.Position == "0,0");
        var second = keys.Single(k => k.Position == "2,1");
        Assert.Equal(["0,0", "2,1", "4,2"], keys.Select(k => k.Position));
        Assert.Equal(OnRunning.Auto, first.Entry!.OnRunning);
        Assert.True(first.Entry.Admin);
        Assert.Equal(OnRunning.Relaunch, second.Entry!.OnRunning);
        Assert.Equal(ImportStatus.Skipped, keys.Single(k => k.Position == "4,2").Status);
    }

    [Fact]
    public void ImportConvertsSquirrelInsideMultiAction()
    {
        using var tmp = new TempDir();
        var update = tmp.Touch("Discord", "Update.exe");
        tmp.Touch("Discord", "app-1.0.9", "Discord.exe");
        var page = Page(tmp, $$$"""
            "0,1": {"UUID":"com.elgato.streamdeck.multiactions.routine","States":[{}],"Actions":[{"Actions":[
              {"UUID":"com.elgato.streamdeck.profile.rotate","Settings":{}},
              {{{Launcher(update, front: false, args: " --processStart Discord.exe")}}}]}]}
            """);

        var key = Assert.Single(ProfileImporter.ReadPage(page, 1, () => []));

        Assert.Equal(TargetKind.Glob, key.Entry!.Kind);
        Assert.Equal(Path.Combine(tmp.Path, "Discord", "app-*", "Discord.exe"), key.Entry.Target);
        Assert.Null(key.Entry.Args);
        Assert.Null(key.Entry.TrackPath);
        Assert.Equal(ImportStatus.Ok, key.Status);
    }

    [Fact]
    public void ImportFlagsMissingTargetButKeepsEntry()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, """
            "1,0": {"UUID":"com.elgato.streamdeck.system.open","States":[{"Title":""}],"Settings":{"path":"\"D:\\gone\\vivaldi.exe\""}}
            """);

        var key = Assert.Single(ProfileImporter.ReadPage(page, 1, () => []));

        Assert.Equal(ImportStatus.Broken, key.Status);
        Assert.Equal(@"D:\gone\vivaldi.exe", key.Entry!.Target);
    }

    [Fact]
    public void ImportMapsStoreLauncherByDisplayName()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, """
            "3,2": {"UUID":"com.barraider.msstorelauncher","States":[{}],"Settings":{"applicationId":"Microsoft Teams"}}
            """);

        var key = Assert.Single(ProfileImporter.ReadPage(page, 1, () => [("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams")]));

        Assert.Equal("MSTeams_8wekyb3d8bbwe!MSTeams", key.Entry!.Target);
        Assert.Equal(TargetKind.Aumid, key.Entry.Kind);
    }

    [Fact]
    public void ImportMapsWindowsCockpitStreamDeckInterfaceOnly()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, """
            "0,0": {"UUID":"com.vivremotion.windowscockpit.plugin","States":[{}],"Settings":{"execute_event_code":"#004"}},
            "1,0": {"UUID":"com.vivremotion.windowscockpit.plugin","States":[{}],"Settings":{"execute_event_code":"#008"}}
            """);

        var keys = ProfileImporter.ReadPage(page, 1, () => []);

        Assert.EndsWith(@"Elgato\StreamDeck\StreamDeck.exe", keys[0].Entry!.Target);
        Assert.Null(keys[1].Entry);
    }

    [Fact]
    public void ImportSkipsElgatoOpenOfNonApp()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, """
            "0,0": {"UUID":"com.elgato.streamdeck.system.open","States":[{}],"Settings":{"path":"C:\\docs\\notes.txt"}}
            """);

        Assert.Equal(ImportStatus.Skipped, Assert.Single(ProfileImporter.ReadPage(page, 1, () => [])).Status);
    }

    [Fact]
    public void LauncherPageHasOnePagedSlotPerKey()
    {
        var bytes = SlotPageBuilder.Build("App Slots", "20GBA9901", "dev", 5, 3, "0.1.0.0");

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var rootEntry = Assert.Single(zip.Entries, e => e.FullName.EndsWith(".sdProfile/manifest.json"));
        var dir = rootEntry.FullName[..rootEntry.FullName.IndexOf('/')];
        Assert.All(zip.Entries, e => Assert.StartsWith(dir + "/", e.FullName));
        using var root = JsonDocument.Parse(rootEntry.Open());
        var pages = root.RootElement.GetProperty("Pages");
        var page = pages.GetProperty("Current").GetString()!;
        var defaultPage = pages.GetProperty("Default").GetString()!;
        Assert.NotEqual(page, defaultPage);
        Assert.NotNull(zip.GetEntry($"{dir}/Profiles/{defaultPage.ToUpperInvariant()}/manifest.json"));
        using var pageDoc = JsonDocument.Parse(zip.GetEntry($"{dir}/Profiles/{page.ToUpperInvariant()}/manifest.json")!.Open());
        var actions = pageDoc.RootElement.GetProperty("Controllers")[0].GetProperty("Actions");

        Assert.Equal("20GBA9901", root.RootElement.GetProperty("Device").GetProperty("Model").GetString());
        Assert.Equal(15, actions.EnumerateObject().Count());
        Assert.Empty(actions.GetProperty("2,1").GetProperty("Settings").EnumerateObject());
        Assert.Equal("com.fulgan.managedapplauncher.slot", actions.GetProperty("4,2").GetProperty("UUID").GetString());
    }

    [Fact]
    public void ShellVerbInstallsAndUninstallsUnderGivenRoot()
    {
        var root = $@"Software\ManagedLauncherTests\{Guid.NewGuid():N}\Classes";
        try
        {
            ShellVerb.Install(@"C:\x\malaunch.exe", root);

            using var command = Registry.CurrentUser.OpenSubKey($@"{root}\lnkfile\shell\{ShellVerb.VerbKey}\command");
            Assert.Equal("\"C:\\x\\malaunch.exe\" pin \"%1\" --notify", command!.GetValue(""));

            ShellVerb.Uninstall(root);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{root}\exefile\shell\{ShellVerb.VerbKey}"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ManagedLauncherTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void EnumeratesStartMenuAppsWithAumids()
    {
        var apps = ManagedLauncher.Core.Icons.ShellItems.EnumerateApps();

        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Aumid)));
    }
}
