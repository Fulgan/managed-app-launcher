using ManagedLauncher.Core.Processes;

namespace ManagedLauncher.Core.Targets;

/// <param name="Target">AUMID for packaged apps, otherwise the full exe path.</param>
public sealed record RunningApp(string Name, string Target, string Detail);

/// <summary>Apps that currently show a window, one per launch target, as candidates to pin.</summary>
public static class RunningApps
{
    /// <summary>Shell hosts whose windows belong to other apps or to Windows itself.</summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "LockApp.exe",
    };

    /// <param name="windows">Visible windows in Z-order with their owner pid.</param>
    /// <param name="aumidNames">Display names of packaged apps by AUMID.</param>
    /// <param name="describe">Friendly name for an exe (its file description), or null.</param>
    public static IReadOnlyList<RunningApp> Collect(
        IReadOnlyList<(int Pid, string Title)> windows,
        IReadOnlyList<ProcessInfo> processes,
        IReadOnlyDictionary<string, string> aumidNames,
        int selfPid,
        Func<string, string?> describe)
    {
        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<RunningApp>();
        foreach (var (pid, title) in windows)
        {
            if (pid == selfPid || !byPid.TryGetValue(pid, out var process))
                continue;
            if (process.ExePath is { } exe && Hosts.Contains(Path.GetFileName(exe)))
                continue;
            var target = process.Aumid ?? process.ExePath;
            if (target is null || !seen.Add(target))
                continue;

            var name = process.Aumid is { } aumid
                ? aumidNames.GetValueOrDefault(aumid) ?? title
                : describe(target) is { Length: > 0 } description ? description : Path.GetFileNameWithoutExtension(target);
            apps.Add(new RunningApp(name, target, process.Aumid is null ? target : $"Store app · {title}"));
        }
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
