namespace ManagedLauncher.Core.Config;

/// <summary>One pinned application. Inherited <see cref="Preset"/> fields override the named preset.</summary>
public class Entry : Preset
{
    public string? Title { get; set; }
    public string Target { get; set; } = "";

    /// <summary>Null means "infer from <see cref="Target"/>" (see <see cref="Targets.TargetKindDetector"/>).</summary>
    public TargetKind? Kind { get; set; }

    /// <summary>Exe to watch for the running indicator when it differs from what gets launched.</summary>
    public string? TrackPath { get; set; }

    /// <summary>Image file used instead of the target's own icon.</summary>
    public string? Icon { get; set; }

    public string Preset { get; set; } = AppConfig.DefaultPresetName;
}
