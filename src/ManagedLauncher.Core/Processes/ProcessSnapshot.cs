using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Processes;

/// <summary>Processes running at one instant. One snapshot is shared by every button for a poll tick.</summary>
public sealed class ProcessSnapshot(IReadOnlyList<ProcessInfo> processes)
{
    public static readonly ProcessSnapshot Empty = new([]);

    public IReadOnlyList<ProcessInfo> Processes { get; } = processes;

    /// <summary>
    /// Matches by full exe path, or by AUMID for AUMID targets without a path. Never by process name alone:
    /// two installed versions of the same app must not be confused. A process with the same file name in a
    /// subfolder of the target's folder also matches, because some launchers start their real app there
    /// (<c>Docker\Docker Desktop.exe</c> → <c>Docker\frontend\Docker Desktop.exe</c>); side-by-side versions
    /// live in sibling folders, never below each other.
    /// </summary>
    public IReadOnlyList<ProcessInfo> Find(ResolvedTarget? target)
    {
        if (target is null || !target.IsLaunchable)
            return [];

        if (target.MatchPath is { } path)
        {
            var name = Path.GetFileName(path);
            var below = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
            return Processes.Where(p => p.ExePath is { } exe
                && (string.Equals(exe, path, StringComparison.OrdinalIgnoreCase)
                    || (exe.StartsWith(below, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetFileName(exe), name, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        if (target.Kind == TargetKind.Aumid && target.Aumid is { } aumid)
            return Processes.Where(p => string.Equals(p.Aumid, aumid, StringComparison.OrdinalIgnoreCase)).ToList();

        return [];
    }

    public bool IsRunning(ResolvedTarget? target) => Find(target).Count > 0;
}
