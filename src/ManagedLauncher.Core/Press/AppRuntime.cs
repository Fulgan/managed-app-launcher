using System.Collections.Concurrent;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

/// <param name="Running">Processes counted as the app when the press was decided.</param>
public sealed record PressOutcome(PressDecision Decision, OperationResult Result, int Running)
{
    public bool Launched => Result.Launched;
}

/// <summary>
/// What pressing an app does and what its indicator shows. The deck and the manager's test key both go through
/// it, so they follow the same rules. Each instance remembers its own recent launches: the launch grace period,
/// the starting state, and the processes a launch started.
/// </summary>
public sealed class AppRuntime(PressExecutor executor, TimeProvider? time = null)
{
    /// <summary>Longest time a launched app shows as starting when it never opens a window (tray and background apps).</summary>
    public static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(60);

    /// <summary>A launch whose processes are all gone after this long failed or was closed; it no longer shows as starting.</summary>
    public static readonly TimeSpan StartupGiveUp = TimeSpan.FromSeconds(10);

    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> launches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Targets launched here that have not shown a window yet, by launch time.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> startups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process each launch started, by target, with its launch time; its process tree counts as the app.</summary>
    private readonly ConcurrentDictionary<string, (int Pid, DateTime Since)> launchedTrees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any of these processes shows a main window yet; ends the starting state.</summary>
    public Func<IReadOnlyCollection<int>, bool> HasWindow { get; set; } =
        pids => WindowFinder.FindMainWindow(pids.ToHashSet()) != IntPtr.Zero;

    public RunState StateOf(Entry entry, ProcessSnapshot snapshot) => StateOf(entry, TargetResolver.Resolve(entry), snapshot);

    /// <summary>
    /// Starting from a launch until one of the app's processes shows a window (or <see cref="StartupLimit"/> passes,
    /// or the launch's processes are all gone after <see cref="StartupGiveUp"/>); then running, or elevated when any
    /// of its processes is.
    /// </summary>
    public RunState StateOf(Entry entry, ResolvedTarget target, ProcessSnapshot snapshot)
    {
        var processes = Matched(entry, target, snapshot);
        var targetKey = TargetKey(entry, target);
        if (startups.TryGetValue(targetKey, out var since))
        {
            var waited = time.GetUtcNow() - since;
            if (waited > StartupLimit
                || (processes.Count == 0 && waited > StartupGiveUp && launchedTrees.ContainsKey(targetKey))
                || (processes.Count > 0 && HasWindow(processes.Select(p => p.Pid).ToList())))
                startups.TryRemove(targetKey, out _);
            else
                return RunState.Starting;
        }
        if (processes.Count == 0)
            return RunState.Stopped;
        return processes.Any(p => p.Elevated) ? RunState.Elevated : RunState.Running;
    }

    /// <summary>
    /// The app's processes: those matching its target, plus everything its last launch here started. The launch's
    /// tree is forgotten once nothing in it runs any more (after the startup period, so a stub that exits before
    /// its child appears doesn't end it early).
    /// </summary>
    public IReadOnlyList<ProcessInfo> Matched(Entry entry, ResolvedTarget target, ProcessSnapshot snapshot)
    {
        var found = snapshot.Find(target);
        var targetKey = TargetKey(entry, target);
        if (!launchedTrees.TryGetValue(targetKey, out var root))
            return found;
        var tree = snapshot.TreeOf(root.Pid, root.Since);
        if (tree.Count == 0)
        {
            if (time.GetUtcNow().UtcDateTime - root.Since > StartupLimit)
                launchedTrees.TryRemove(targetKey, out _);
            return found;
        }
        return found.Concat(tree).DistinctBy(p => p.Pid).ToList();
    }

    /// <summary>Decides and carries out a press; <paramref name="beforeClose"/> runs once a long press is known to close.</summary>
    public async Task<PressOutcome> PressAsync(Entry entry, bool isLong, ProcessSnapshot snapshot, AppConfig config, Func<Task>? beforeClose = null)
    {
        var target = TargetResolver.Resolve(entry);
        var options = LaunchOptions.Resolve(entry);
        var running = Matched(entry, target, snapshot);
        var targetKey = TargetKey(entry, target);
        var decision = ActionPlanner.Plan(isLong, running.Count > 0, options, target, IsStarting(targetKey, running, config));

        if (isLong && decision is PressDecision.Close && beforeClose is not null)
            await beforeClose().ConfigureAwait(false);

        var result = await executor.ExecuteAsync(decision, target, options, running, TimeSpan.FromMilliseconds(config.CloseTimeoutMs))
            .ConfigureAwait(false);
        var outcome = new PressOutcome(decision, result, running.Count);
        if (outcome.Launched)
        {
            launches[targetKey] = startups[targetKey] = time.GetUtcNow();
            if (result.StartedPid is { } pid)
                launchedTrees[targetKey] = (pid, time.GetUtcNow().UtcDateTime.AddSeconds(-1));
        }
        return outcome;
    }

    private bool IsStarting(string targetKey, IReadOnlyList<ProcessInfo> running, AppConfig config)
    {
        var grace = TimeSpan.FromMilliseconds(config.LaunchGraceMs);
        var now = time.GetUtcNow();
        if (launches.TryGetValue(targetKey, out var launched) && now - launched < grace)
            return true;
        return running.Any(p => now.UtcDateTime - p.StartTime < grace);
    }

    private static string TargetKey(Entry entry, ResolvedTarget target) =>
        target.MatchPath ?? target.Aumid ?? target.LaunchFile ?? entry.Target;
}
