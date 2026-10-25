using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Tests;

public class ProcessTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ResolvedTarget PathTarget(string path) =>
        new(TargetKind.Exe, path, null, path, null, null, null, path, null);

    [Fact]
    public void MatchesByFullPathIgnoringCaseNotByName()
    {
        var snapshot = new ProcessSnapshot([
            new ProcessInfo(1, @"C:\Apps\app-1.0\Tool.exe", null, T0),
            new ProcessInfo(2, @"C:\Apps\app-2.0\Tool.exe", null, T0),
        ]);

        var found = snapshot.Find(PathTarget(@"c:\apps\APP-2.0\tool.exe"));

        Assert.Equal([2], found.Select(p => p.Pid));
    }

    [Fact]
    public void SameExeNameInSubfolderOfTargetMatches()
    {
        var snapshot = new ProcessSnapshot([
            new ProcessInfo(1, @"C:\Program Files\Docker\Docker\frontend\Docker Desktop.exe", null, T0),
            new ProcessInfo(2, @"C:\Program Files\Docker\Other\Docker Desktop.exe", null, T0),
            new ProcessInfo(3, @"C:\Program Files\Docker\Docker\frontend\helper.exe", null, T0),
        ]);

        var found = snapshot.Find(PathTarget(@"C:\Program Files\Docker\Docker\Docker Desktop.exe"));

        Assert.Equal([1], found.Select(p => p.Pid));
    }

    [Fact]
    public void SiblingVersionFolderDoesNotMatch()
    {
        var snapshot = new ProcessSnapshot([new ProcessInfo(1, @"C:\Apps\app-1.0\Tool.exe", null, T0)]);

        Assert.False(snapshot.IsRunning(PathTarget(@"C:\Apps\app-2.0\Tool.exe")));
    }

    [Fact]
    public void AumidTargetsMatchByAumid()
    {
        var snapshot = new ProcessSnapshot([
            new ProcessInfo(1, @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", "MSTeams_8wekyb3d8bbwe!MSTeams", T0),
        ]);
        var target = new ResolvedTarget(TargetKind.Aumid, null, "msteams_8wekyb3d8bbwe!msteams", null, null, null, null, null, null);

        Assert.True(snapshot.IsRunning(target));
    }

    [Fact]
    public void UnlaunchableTargetIsNeverRunning()
    {
        var snapshot = new ProcessSnapshot([new ProcessInfo(1, null, null, T0)]);

        Assert.False(snapshot.IsRunning(ResolvedTarget.Failed(TargetKind.Exe, "x")));
    }

    [Fact]
    public void TrackerTakesOneSnapshotPerTickAndReportsOnlyChanges()
    {
        var source = new FakeSource();
        var tracker = new ProcessTracker(source);
        var changes = new List<(string, bool)>();
        tracker.RunningChanged += (k, r) => changes.Add((k, r));
        tracker.Subscribe("a", () => PathTarget(@"C:\a.exe"));
        tracker.Subscribe("b", () => PathTarget(@"C:\b.exe"));

        source.Processes = [new ProcessInfo(1, @"C:\a.exe", null, T0)];
        tracker.Refresh();
        tracker.Refresh();
        source.Processes = [];
        tracker.Refresh();

        Assert.Equal(3, source.Calls);
        Assert.Equal([("a", true), ("b", false), ("a", false)], changes);
    }

    [Fact]
    public void RealSnapshotContainsCurrentProcessWithPath()
    {
        var self = new Win32ProcessSource().Snapshot().SingleOrDefault(p => p.Pid == Environment.ProcessId);

        Assert.NotNull(self);
        Assert.Equal(Environment.ProcessPath, self.ExePath, ignoreCase: true);
    }

    private sealed class FakeSource : IProcessSource
    {
        public IReadOnlyList<ProcessInfo> Processes { get; set; } = [];
        public int Calls { get; private set; }

        public IReadOnlyList<ProcessInfo> Snapshot()
        {
            Calls++;
            return Processes;
        }
    }
}
