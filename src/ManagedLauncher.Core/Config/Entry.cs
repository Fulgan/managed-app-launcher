using System.Text.Json.Serialization;

namespace ManagedLauncher.Core.Config;

/// <summary>
/// One list item: a pinned application with its own launch settings, a layout break, a folder (owns another stored
/// list) or a link (opens another stored list without owning it).
/// </summary>
public class Entry : LaunchSettings
{
    /// <summary>Set for a layout item instead of an app: the next app starts on a new row or page. Target is then empty.</summary>
    public ListBreak? Break { get; set; }

    [JsonIgnore]
    public bool IsBreak => Break is not null;

    /// <summary>Id of the stored list this folder item owns.</summary>
    public string? Folder { get; set; }

    /// <summary>Id of the stored list this link item opens.</summary>
    public string? Link { get; set; }

    [JsonIgnore]
    public bool IsFolder => !IsBreak && Folder is not null;

    [JsonIgnore]
    public bool IsLink => !IsBreak && Folder is null && Link is not null;

    [JsonIgnore]
    public bool IsApp => !IsBreak && Folder is null && Link is null;

    /// <summary>The list a folder or link item opens; null for apps and breaks.</summary>
    [JsonIgnore]
    public string? Opens => IsFolder ? Folder : IsLink ? Link : null;

    /// <summary>Set on load for a link whose target leads back to the list holding it; such a link doesn't open.</summary>
    [JsonIgnore]
    public string? LinkProblem { get; internal set; }

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
