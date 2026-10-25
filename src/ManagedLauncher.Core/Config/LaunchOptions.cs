namespace ManagedLauncher.Core.Config;

/// <summary>Fully resolved options for one entry: its own settings, else the built-in defaults.</summary>
public sealed record LaunchOptions(
    string? Args,
    string? WorkDir,
    bool Admin,
    OnRunning OnRunning,
    KillMode KillMode,
    IndicatorStyle Indicator,
    LongPressMode LongPress)
{
    public static readonly LaunchOptions Defaults = new(null, null, false, OnRunning.Auto, KillMode.All, IndicatorStyle.Dot, LongPressMode.Auto);

    public static LaunchOptions Resolve(LaunchSettings entry) => new(
        entry.Args,
        entry.WorkDir,
        entry.Admin ?? Defaults.Admin,
        entry.OnRunning ?? Defaults.OnRunning,
        entry.KillMode ?? Defaults.KillMode,
        entry.Indicator ?? Defaults.Indicator,
        entry.LongPress ?? Defaults.LongPress);
}
