using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

/// <param name="StartedPid">Process a launch created, when Windows reports one; lets callers follow what it starts.</param>
public sealed record OperationResult(bool Success, string? Error = null, int? StartedPid = null)
{
    public static readonly OperationResult Ok = new(true);

    public static OperationResult Started(int? pid) => new(true, null, pid);

    public static OperationResult Fail(string error) => new(false, error);
}

/// <summary>Side-effecting operations on applications. Separated so press handling can be tested without launching anything.</summary>
public interface IAppOperations
{
    OperationResult Launch(ResolvedTarget target, LaunchOptions options, bool elevated);

    /// <summary>Brings a window of one of the processes to the front. False when none has a window that can be shown.</summary>
    bool TryFocus(IReadOnlyList<ProcessInfo> processes);

    Task<OperationResult> CloseAsync(IReadOnlyList<ProcessInfo> processes, TimeSpan timeout);
}
