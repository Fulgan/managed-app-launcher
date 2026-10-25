namespace ManagedLauncher.Core.Processes;

/// <param name="ExePath">Full image path; null when the process can't be queried (protected or already gone).</param>
/// <param name="Aumid">App user model id; null for processes without package identity.</param>
/// <param name="Elevated">Runs with an elevated token, or its token can't be read (a higher-privileged process).</param>
/// <param name="ParentPid">Process that created it; Windows keeps the number even after that process exits.</param>
public sealed record ProcessInfo(int Pid, string? ExePath, string? Aumid, DateTime StartTime, bool Elevated = false, int ParentPid = 0);

public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> Snapshot();
}
