using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

public enum PressDecision
{
    Nothing,
    Alert,
    Launch,
    LaunchAdmin,
    Focus,
    FocusOrRelaunch,

    /// <summary>App is still starting: bring its window up if one exists yet, never start another instance.</summary>
    FocusIfAny,
    Relaunch,
    Close,
}

/// <summary>Maps a press to what should happen, from the key's state alone. No side effects.</summary>
public static class ActionPlanner
{
    /// <param name="starting">Launched moments ago, or its newest process is younger than the launch grace period.</param>
    public static PressDecision Plan(bool isLong, bool running, LaunchOptions options, ResolvedTarget target, bool starting = false)
    {
        if (!target.IsLaunchable)
            return PressDecision.Alert;

        var decision = PlanSettled(isLong, running, options, target);
        return starting && decision is PressDecision.Launch or PressDecision.LaunchAdmin or PressDecision.Relaunch or PressDecision.FocusOrRelaunch
            ? PressDecision.FocusIfAny
            : decision;
    }

    private static PressDecision PlanSettled(bool isLong, bool running, LaunchOptions options, ResolvedTarget target)
    {
        var packaged = target.Kind is TargetKind.Aumid or TargetKind.Url;
        if (isLong && running && options.LongPress == LongPressMode.Auto)
            return PressDecision.Close;

        if (!running)
            return LaunchDecision(options, packaged);

        return options.OnRunning switch
        {
            OnRunning.Focus => PressDecision.Focus,
            OnRunning.Relaunch => LaunchDecision(options, packaged) == PressDecision.Alert ? PressDecision.Alert : PressDecision.Relaunch,
            OnRunning.Auto => PressDecision.FocusOrRelaunch,
            _ => PressDecision.Nothing,
        };
    }

    /// <summary>Packaged apps and links can't be started elevated through the shell, so the admin option on one is an error.</summary>
    private static PressDecision LaunchDecision(LaunchOptions options, bool packaged) =>
        !options.Admin ? PressDecision.Launch
        : packaged ? PressDecision.Alert
        : PressDecision.LaunchAdmin;
}
