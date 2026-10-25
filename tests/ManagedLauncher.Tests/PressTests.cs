using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;
using Microsoft.Extensions.Time.Testing;

namespace ManagedLauncher.Tests;

public class PressTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(2000);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ResolvedTarget Exe = new(TargetKind.Exe, @"C:\a.exe", null, @"C:\a.exe", null, null, null, null, null);
    private static readonly ResolvedTarget Packaged = new(TargetKind.Aumid, null, "X!App", null, null, null, null, null, null);

    private static LaunchOptions Options(
        OnRunning onRunning = OnRunning.Auto, bool admin = false, LongPressMode longPress = LongPressMode.Auto,
        KillMode kill = KillMode.All) =>
        new(null, null, admin, onRunning, kill, IndicatorStyle.Border, longPress);

    [Theory]
    [InlineData(false, false, OnRunning.Auto, PressDecision.Launch)]
    [InlineData(false, true, OnRunning.Auto, PressDecision.FocusOrRelaunch)]
    [InlineData(false, true, OnRunning.Focus, PressDecision.Focus)]
    [InlineData(false, true, OnRunning.Relaunch, PressDecision.Relaunch)]
    [InlineData(false, true, OnRunning.None, PressDecision.Nothing)]
    [InlineData(true, true, OnRunning.Auto, PressDecision.Close)]
    [InlineData(true, false, OnRunning.Auto, PressDecision.Launch)]
    public void PlansFromStateAndOptions(bool isLong, bool running, OnRunning onRunning, PressDecision expected)
    {
        Assert.Equal(expected, ActionPlanner.Plan(isLong, running, Options(onRunning), Exe));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StartingAppIsOnlyFocused(bool isLong, bool running)
    {
        Assert.Equal(PressDecision.FocusIfAny, ActionPlanner.Plan(isLong, running, Options(), Exe, starting: true));
    }

    [Fact]
    public void StartingAppCanStillBeClosed()
    {
        Assert.Equal(PressDecision.Close, ActionPlanner.Plan(true, true, Options(), Exe, starting: true));
    }

    [Fact]
    public void AdminOptionLaunchesElevatedOnShortPress()
    {
        Assert.Equal(PressDecision.LaunchAdmin, ActionPlanner.Plan(false, false, Options(admin: true), Exe));
    }

    [Fact]
    public void LongPressOnStoppedAppActsAsShortPress()
    {
        Assert.Equal(PressDecision.Launch, ActionPlanner.Plan(true, false, Options(), Packaged));
        Assert.Equal(PressDecision.Launch, ActionPlanner.Plan(true, false, Options(), Exe));
    }

    [Fact]
    public void LongPressNoneActsAsShort()
    {
        Assert.Equal(PressDecision.FocusOrRelaunch, ActionPlanner.Plan(true, true, Options(longPress: LongPressMode.None), Exe));
        Assert.Equal(PressDecision.Launch, ActionPlanner.Plan(true, false, Options(longPress: LongPressMode.None), Exe));
    }

    [Fact]
    public void BrokenTargetAlerts()
    {
        Assert.Equal(PressDecision.Alert, ActionPlanner.Plan(false, false, Options(), ResolvedTarget.Failed(TargetKind.Glob, "no match")));
    }

    [Fact]
    public void ReleaseBeforeThresholdIsShort()
    {
        var time = new FakeTimeProvider();
        var timer = new PressTimer(time);
        var fired = new List<string>();

        timer.KeyDown("k", Threshold, () => fired.Add("short"), () => fired.Add("long"));
        time.Advance(TimeSpan.FromMilliseconds(1999));
        timer.KeyUp("k");
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["short"], fired);
    }

    [Fact]
    public void LongFiresWhileHeldAndReleaseIsIgnored()
    {
        var time = new FakeTimeProvider();
        var timer = new PressTimer(time);
        var fired = new List<string>();

        timer.KeyDown("k", Threshold, () => fired.Add("short"), () => fired.Add("long"));
        time.Advance(Threshold);
        Assert.Equal(["long"], fired);

        timer.KeyUp("k");
        Assert.Equal(["long"], fired);
    }

    [Fact]
    public void WithoutLongActionReleaseIsAlwaysShort()
    {
        var time = new FakeTimeProvider();
        var timer = new PressTimer(time);
        var fired = new List<string>();

        timer.KeyDown("k", Threshold, () => fired.Add("short"), null);
        time.Advance(TimeSpan.FromSeconds(10));
        timer.KeyUp("k");

        Assert.Equal(["short"], fired);
    }

    [Fact]
    public void KeysAreIndependentAndCancelFiresNothing()
    {
        var time = new FakeTimeProvider();
        var timer = new PressTimer(time);
        var fired = new List<string>();

        timer.KeyDown("a", Threshold, () => fired.Add("a-short"), () => fired.Add("a-long"));
        timer.KeyDown("b", Threshold, () => fired.Add("b-short"), () => fired.Add("b-long"));
        timer.KeyUp("b");
        timer.Cancel("a");
        time.Advance(Threshold);
        timer.KeyUp("a");

        Assert.Equal(["b-short"], fired);
    }

    [Fact]
    public async Task FocusOrRelaunchLaunchesWhenNoWindow()
    {
        var ops = new FakeOperations { CanFocus = false };

        var result = await new PressExecutor(ops).ExecuteAsync(PressDecision.FocusOrRelaunch, Exe, Options(), [], TimeSpan.Zero);

        Assert.True(result.Success);
        Assert.Equal(["focus", "launch:False"], ops.Calls);
    }

    [Fact]
    public async Task FocusOrRelaunchStopsAfterFocus()
    {
        var ops = new FakeOperations { CanFocus = true };

        await new PressExecutor(ops).ExecuteAsync(PressDecision.FocusOrRelaunch, Exe, Options(), [], TimeSpan.Zero);

        Assert.Equal(["focus"], ops.Calls);
    }

    [Fact]
    public async Task FocusWithoutWindowFails()
    {
        var ops = new FakeOperations { CanFocus = false };

        var result = await new PressExecutor(ops).ExecuteAsync(PressDecision.Focus, Exe, Options(), [], TimeSpan.Zero);

        Assert.False(result.Success);
    }

    [Fact]
    public void NewestKillModePicksLatestStart()
    {
        ProcessInfo[] running = [new(1, "a", null, T0), new(2, "a", null, T0.AddMinutes(5)), new(3, "a", null, T0.AddMinutes(1))];

        Assert.Equal([2], PressExecutor.SelectForClose(running, KillMode.Newest).Select(p => p.Pid));
        Assert.Equal(3, PressExecutor.SelectForClose(running, KillMode.All).Count);
    }

    [Fact]
    public void ShortcutLaunchesAsIsUnlessSettingsAddArgs()
    {
        var lnk = new ResolvedTarget(TargetKind.Lnk, @"C:\s\App.lnk", null, @"C:\p\app.exe", @"C:\p\app.exe", "--a", @"C:\p", null, null);

        var plain = Win32AppOperations.BuildStartInfo(lnk, Options(), elevated: false);
        var withArgs = Win32AppOperations.BuildStartInfo(lnk, Options() with { Args = "--b" }, elevated: true);

        Assert.Equal(@"C:\s\App.lnk", plain.FileName);
        Assert.Equal(@"C:\p\app.exe", withArgs.FileName);
        Assert.Equal("--a --b", withArgs.Arguments);
        Assert.Equal(@"C:\p", withArgs.WorkingDirectory);
        Assert.Equal("runas", withArgs.Verb);
    }

    [Fact]
    public void ExeStartsInItsOwnDirectoryByDefault()
    {
        var info = Win32AppOperations.BuildStartInfo(Exe, Options(), elevated: false);

        Assert.Equal(@"C:\", info.WorkingDirectory);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public async Task CloseKillsProcessThatIgnoresCloseRequest()
    {
        using var tmp = new TempDir();
        var exe = tmp.File("pinger.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var child = StartQuiet(exe);
        var info = new ProcessInfo(child.Id, exe, null, child.StartTime.ToUniversalTime());

        var result = await new Win32AppOperations().CloseAsync([info], TimeSpan.FromMilliseconds(300));

        Assert.True(result.Success);
        Assert.True(child.WaitForExit(5000));
    }

    [Fact]
    public async Task CloseNeverTouchesProcessesUnderWindowsFolder()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var child = StartQuiet(exe);
        try
        {
            var info = new ProcessInfo(child.Id, exe, null, child.StartTime.ToUniversalTime());

            var result = await new Win32AppOperations().CloseAsync([info], TimeSpan.FromMilliseconds(100));

            Assert.False(result.Success);
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
            child.WaitForExit(5000);
        }
    }

    [Fact]
    public async Task CloseSkipsRecycledPid()
    {
        using var tmp = new TempDir();
        var exe = tmp.File("pinger.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var child = StartQuiet(exe);
        try
        {
            var stale = new ProcessInfo(child.Id, exe, null, child.StartTime.ToUniversalTime().AddMinutes(-5));

            await new Win32AppOperations().CloseAsync([stale], TimeSpan.FromMilliseconds(100));

            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
            child.WaitForExit(5000);
        }
    }

    private static System.Diagnostics.Process StartQuiet(string exe) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

    private sealed class FakeOperations : IAppOperations
    {
        public bool CanFocus { get; init; }
        public List<string> Calls { get; } = [];

        public OperationResult Launch(ResolvedTarget target, LaunchOptions options, bool elevated)
        {
            Calls.Add($"launch:{elevated}");
            return OperationResult.Ok;
        }

        public bool TryFocus(IReadOnlyList<ProcessInfo> processes)
        {
            Calls.Add("focus");
            return CanFocus;
        }

        public Task<OperationResult> CloseAsync(IReadOnlyList<ProcessInfo> processes, TimeSpan timeout)
        {
            Calls.Add("close");
            return Task.FromResult(OperationResult.Ok);
        }
    }
}
