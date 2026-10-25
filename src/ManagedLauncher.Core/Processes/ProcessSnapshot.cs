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
    /// live in sibling folders, never below each other. A match path ending in a separator names a folder: any
    /// process running from it or below matches (a game started through a launcher link). A target directly in a drive root gets no subfolder match:
    /// that would claim every process of that name on the drive.
    /// </summary>
    public IReadOnlyList<ProcessInfo> Find(ResolvedTarget? target)
    {
        if (target is null || !target.IsLaunchable)
            return [];

        if (target.MatchPath is { } dir && dir.EndsWith(Path.DirectorySeparatorChar))
            return Processes.Where(p => p.ExePath?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (target.MatchPath is { } path)
        {
            var name = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path);
            var atRoot = folder is null || string.Equals(folder, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
            var below = atRoot ? null : folder + Path.DirectorySeparatorChar;
            return Processes.Where(p => p.ExePath is { } exe
                && (string.Equals(exe, path, StringComparison.OrdinalIgnoreCase)
                    || (below is not null && exe.StartsWith(below, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetFileName(exe), name, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        if (target.Kind == TargetKind.Aumid && target.Aumid is { } aumid)
            return Processes.Where(p => string.Equals(p.Aumid, aumid, StringComparison.OrdinalIgnoreCase)).ToList();

        return [];
    }

    public bool IsRunning(ResolvedTarget? target) => Find(target).Count > 0;

    /// <summary>
    /// The process <paramref name="pid"/> and everything it started, directly or through children, as long as those
    /// still run. Launcher stubs (package-manager shims, extract-and-run wrappers) hand over to such children.
    /// Processes older than <paramref name="since"/> are not part of the tree: their parent number belongs to a
    /// recycled pid.
    /// </summary>
    public IReadOnlyList<ProcessInfo> TreeOf(int pid, DateTime since)
    {
        var tree = Processes.Where(p => p.Pid == pid && p.StartTime >= since).ToList();
        var parents = new HashSet<int> { pid };
        for (var added = true; added;)
        {
            added = false;
            foreach (var p in Processes)
            {
                if (p.StartTime >= since && parents.Contains(p.ParentPid) && parents.Add(p.Pid))
                {
                    tree.Add(p);
                    added = true;
                }
            }
        }
        return tree;
    }
}
