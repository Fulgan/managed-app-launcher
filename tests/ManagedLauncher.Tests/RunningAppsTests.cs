using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Tests;

public class RunningAppsTests
{
    private static ProcessInfo Proc(int pid, string? exe, string? aumid = null) => new(pid, exe, aumid, DateTime.UtcNow);

    private static IReadOnlyList<RunningApp> Collect(
        IReadOnlyList<(int, string)> windows, IReadOnlyList<ProcessInfo> processes, int self = 1,
        Dictionary<string, string>? names = null, Func<string, string?>? describe = null) =>
        RunningApps.Collect(windows, processes, names ?? [], self, describe ?? (_ => null));

    [Fact]
    public void OneEntryPerExeEvenWithManyWindowsOrProcesses()
    {
        var apps = Collect(
            [(10, "Mozilla Firefox"), (10, "Library"), (11, "Private")],
            [Proc(10, @"C:\Program Files\Mozilla Firefox\firefox.exe"), Proc(11, @"C:\Program Files\Mozilla Firefox\firefox.exe")]);

        var app = Assert.Single(apps);
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", app.Target);
        Assert.Equal("firefox", app.Name);
    }

    [Fact]
    public void PackagedAppUsesAumidAndStartMenuName()
    {
        var apps = Collect(
            [(20, "Chat | Microsoft Teams")],
            [Proc(20, @"C:\Program Files\WindowsApps\MSTeams_x\ms-teams.exe", "MSTeams_8wekyb3d8bbwe!MSTeams")],
            names: new() { ["MSTeams_8wekyb3d8bbwe!MSTeams"] = "Microsoft Teams" });

        var app = Assert.Single(apps);
        Assert.Equal("MSTeams_8wekyb3d8bbwe!MSTeams", app.Target);
        Assert.Equal("Microsoft Teams", app.Name);
    }

    [Fact]
    public void SkipsSelfShellHostsAndUnknownProcesses()
    {
        var apps = Collect(
            [(1, "Managed App Launcher"), (30, "Settings"), (31, "gone")],
            [Proc(1, @"C:\x\ManagedLauncher.Manager.exe"), Proc(30, @"C:\Windows\System32\ApplicationFrameHost.exe")]);

        Assert.Empty(apps);
    }

    [Fact]
    public void FileDescriptionNamesExeAndResultIsSorted()
    {
        var apps = Collect(
            [(40, "a"), (41, "b")],
            [Proc(40, @"C:\z\zed.exe"), Proc(41, @"C:\a\notepad.exe")],
            describe: p => p.EndsWith("zed.exe") ? "Alpha Editor" : null);

        Assert.Equal(["Alpha Editor", "notepad"], apps.Select(a => a.Name));
    }
}
