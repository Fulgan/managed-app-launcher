namespace ManagedLauncher.Core.Config;

/// <summary>
/// How an app is launched and handled on the deck. Every field is nullable: an app only states what it changes,
/// and <see cref="LaunchOptions.Resolve"/> fills the rest with built-in defaults.
/// </summary>
public class LaunchSettings
{
    public string? Args { get; set; }
    public string? WorkDir { get; set; }
    public bool? Admin { get; set; }
    public OnRunning? OnRunning { get; set; }
    public KillMode? KillMode { get; set; }
    public IndicatorStyle? Indicator { get; set; }
    public LongPressMode? LongPress { get; set; }

    /// <summary>Fills this entry's unset fields from <paramref name="other"/>.</summary>
    public void FillFrom(LaunchSettings? other)
    {
        if (other is null)
            return;
        Args ??= other.Args;
        WorkDir ??= other.WorkDir;
        Admin ??= other.Admin;
        OnRunning ??= other.OnRunning;
        KillMode ??= other.KillMode;
        Indicator ??= other.Indicator;
        LongPress ??= other.LongPress;
    }
}
