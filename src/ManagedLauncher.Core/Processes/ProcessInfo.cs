namespace ManagedLauncher.Core.Processes;

/// <param name="ExePath">Full image path; null when the process can't be queried (protected or already gone).</param>
/// <param name="Aumid">App user model id; null for processes without package identity.</param>
public sealed record ProcessInfo(int Pid, string? ExePath, string? Aumid, DateTime StartTime);

public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> Snapshot();
}
