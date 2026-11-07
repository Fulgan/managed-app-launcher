namespace ManagedLauncher.Core.Config;

/// <summary>How the launcher behaves while it shows one list.</summary>
public sealed class ListOptions
{
    /// <summary>After an app press launches or focuses something, leave the launcher (Back, or the first page).</summary>
    public bool ReturnAfterLaunch { get; set; }

    public string? Icon { get; set; }

    /// <summary>Deepest folder level allowed in this list and below it; top-level lists are level 1.</summary>
    public int? MaxDepth { get; set; }

    /// <summary>Page keys of this list and of folders below it that set none; null takes the parent's.</summary>
    public Navigation? Navigation { get; set; }

    /// <summary>Back or Up on the first key of every page instead of the first page only; null takes the parent's.</summary>
    public bool? BackOnEveryPage { get; set; }

    /// <summary>Tile colour of the control and folder keys shown for this list and folders below it that set none.</summary>
    public KeyTheme? Theme { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => !ReturnAfterLaunch && Icon is null && MaxDepth is null && Navigation is null && BackOnEveryPage is null && Theme is null;

    public ListOptions Clone() => new() { ReturnAfterLaunch = ReturnAfterLaunch, Icon = Icon, MaxDepth = MaxDepth, Navigation = Navigation, BackOnEveryPage = BackOnEveryPage, Theme = Theme };
}
