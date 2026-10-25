using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

public sealed record OperationResult(bool Success, string? Error = null)
{
    public static readonly OperationResult Ok = new(true);

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
