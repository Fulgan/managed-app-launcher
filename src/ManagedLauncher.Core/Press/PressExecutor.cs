using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

public sealed class PressExecutor(IAppOperations operations)
{
    /// <param name="running">Processes matching the target at press time.</param>
    public async Task<OperationResult> ExecuteAsync(
        PressDecision decision, ResolvedTarget target, LaunchOptions options,
        IReadOnlyList<ProcessInfo> running, TimeSpan closeTimeout)
    {
        switch (decision)
        {
            case PressDecision.Nothing:
                return OperationResult.Ok;
            case PressDecision.Alert:
                return OperationResult.Fail(target.Error ?? "not supported for this target");
            case PressDecision.Launch:
            case PressDecision.Relaunch:
                return operations.Launch(target, options, options.Admin);
            case PressDecision.LaunchAdmin:
                return operations.Launch(target, options, elevated: true);
            case PressDecision.Focus:
                return operations.TryFocus(running) ? OperationResult.Ok : OperationResult.Fail("no window to focus");
            case PressDecision.FocusIfAny:
                operations.TryFocus(running);
                return OperationResult.Ok;
            case PressDecision.FocusOrRelaunch:
                return operations.TryFocus(running) ? OperationResult.Ok : operations.Launch(target, options, options.Admin);
            case PressDecision.Close:
                return await operations.CloseAsync(SelectForClose(running, options.KillMode), closeTimeout).ConfigureAwait(false);
            default:
                return OperationResult.Fail($"unhandled decision {decision}");
        }
    }

    public static IReadOnlyList<ProcessInfo> SelectForClose(IReadOnlyList<ProcessInfo> running, KillMode mode) =>
        mode == KillMode.Newest && running.Count > 1
            ? [running.MaxBy(p => p.StartTime)!]
            : running;
}
