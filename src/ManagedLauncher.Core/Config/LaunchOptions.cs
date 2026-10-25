namespace ManagedLauncher.Core.Config;

/// <summary>Fully resolved options for one entry: entry override, then named preset, then <c>default</c> preset, then built-in default.</summary>
public sealed record LaunchOptions(
    string? Args,
    string? WorkDir,
    bool Admin,
    OnRunning OnRunning,
    KillMode KillMode,
    IndicatorStyle Indicator,
    LongPressMode LongPress)
{
    public static LaunchOptions Resolve(AppConfig config, Entry entry, Action<string>? log = null)
    {
        var fallback = config.Presets.GetValueOrDefault(AppConfig.DefaultPresetName) ?? new Preset();
        if (!config.Presets.TryGetValue(entry.Preset ?? AppConfig.DefaultPresetName, out var preset))
        {
            log?.Invoke($"unknown preset '{entry.Preset}', using '{AppConfig.DefaultPresetName}'");
            preset = fallback;
        }

        return new LaunchOptions(
            entry.Args ?? preset.Args ?? fallback.Args,
            entry.WorkDir ?? preset.WorkDir ?? fallback.WorkDir,
            entry.Admin ?? preset.Admin ?? fallback.Admin ?? false,
            entry.OnRunning ?? preset.OnRunning ?? fallback.OnRunning ?? OnRunning.Auto,
            entry.KillMode ?? preset.KillMode ?? fallback.KillMode ?? KillMode.All,
            entry.Indicator ?? preset.Indicator ?? fallback.Indicator ?? IndicatorStyle.Dot,
            entry.LongPress ?? preset.LongPress ?? fallback.LongPress ?? LongPressMode.Auto);
    }
}
