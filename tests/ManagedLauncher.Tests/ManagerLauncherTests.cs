using ManagedLauncher.Core.Processes;
using ManagedLauncher.Plugin;

namespace ManagedLauncher.Tests;

public class ManagerLauncherTests
{
    private const string Exe = @"C:\plugin\ManagedLauncher.Manager.exe";

    private sealed class Fake
    {
        public List<ProcessInfo> Running { get; } = [];
        public int Starts { get; private set; }
        public List<int> Focused { get; } = [];
        public bool HasWindow { get; set; } = true;
        public bool CanStart { get; set; } = true;

        public ManagerLauncher Launcher(string exe = Exe) => new(exe, new Source(this), Start, Focus, _ => { });

        private int? Start(string path)
        {
            if (!CanStart)
                return null;
            Starts++;
            var pid = 1000 + Starts;
            lock (Running)
                Running.Add(new ProcessInfo(pid, path, null, DateTime.UtcNow));
            return pid;
        }

        private bool Focus(int pid)
        {
            if (!HasWindow)
                return false;
            lock (Focused)
                Focused.Add(pid);
            return true;
        }

        private sealed class Source(Fake fake) : IProcessSource
        {
            public IReadOnlyList<ProcessInfo> Snapshot()
            {
                lock (fake.Running)
                    return fake.Running.ToList();
            }
        }
    }

    [Fact]
    public void StartsWhenNotRunningAndFocusesNewWindow()
    {
        var fake = new Fake();

        Assert.True(fake.Launcher().Open());

        Assert.Equal(1, fake.Starts);
        Assert.Contains(1001, fake.Focused);
    }

    [Fact]
    public void RunningInstanceIsFocusedNotStartedAgain()
    {
        var fake = new Fake();
        fake.Running.Add(new ProcessInfo(42, @"c:\PLUGIN\ManagedLauncher.Manager.exe", null, DateTime.UtcNow));

        Assert.True(fake.Launcher().Open());

        Assert.Equal(0, fake.Starts);
        Assert.Equal([42], fake.Focused);
    }

    [Fact]
    public void OtherExeWithSameNameIsIgnored()
    {
        var fake = new Fake();
        fake.Running.Add(new ProcessInfo(42, @"D:\elsewhere\ManagedLauncher.Manager.exe", null, DateTime.UtcNow));

        fake.Launcher().Open();

        Assert.Equal(1, fake.Starts);
    }

    [Fact]
    public async Task RapidPressesStartOnlyOnce()
    {
        var fake = new Fake { HasWindow = false };
        var launcher = fake.Launcher();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(launcher.Open)));

        Assert.Equal(1, fake.Starts);
    }

    [Fact]
    public void FailedStartReportsFalse()
    {
        var fake = new Fake { CanStart = false };

        Assert.False(fake.Launcher().Open());
    }
}
