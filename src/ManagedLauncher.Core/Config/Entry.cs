using System.Text.Json.Serialization;

namespace ManagedLauncher.Core.Config;

/// <summary>One pinned application with its own launch settings.</summary>
public class Entry : LaunchSettings
{
    /// <summary>Set for a layout item instead of an app: the next app starts on a new row or page. Target is then empty.</summary>
    public ListBreak? Break { get; set; }

    [JsonIgnore]
    public bool IsBreak => Break is not null;

    [JsonIgnore]
    public Paging.ListItemKind LayoutKind => Break switch
    {
        ListBreak.Row => Paging.ListItemKind.RowBreak,
        ListBreak.Page => Paging.ListItemKind.PageBreak,
        _ => Paging.ListItemKind.App,
    };

    public string? Title { get; set; }
    public string Target { get; set; } = "";

    /// <summary>Null means "infer from <see cref="Target"/>" (see <see cref="Targets.TargetKindDetector"/>).</summary>
    public TargetKind? Kind { get; set; }

    /// <summary>Exe to watch for the running indicator when it differs from what gets launched.</summary>
    public string? TrackPath { get; set; }

    /// <summary>Image file used instead of the target's own icon.</summary>
    public string? Icon { get; set; }

    /// <summary>Shared settings group of older configs; folded into the entry on load and never written back.</summary>
    [JsonInclude, JsonPropertyName("preset")]
    internal string? LegacyPreset { get; set; }
}
