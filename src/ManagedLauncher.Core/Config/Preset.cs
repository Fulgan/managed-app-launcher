namespace ManagedLauncher.Core.Config;

/// <summary>
/// Launch options. Every field is nullable so a preset, or an entry overriding a preset,
/// only states what it changes; <see cref="LaunchOptions.Resolve"/> fills the gaps.
/// </summary>
public class Preset
{
    public string? Args { get; set; }
    public string? WorkDir { get; set; }
    public bool? Admin { get; set; }
    public OnRunning? OnRunning { get; set; }
    public KillMode? KillMode { get; set; }
    public IndicatorStyle? Indicator { get; set; }
    public LongPressMode? LongPress { get; set; }
}
