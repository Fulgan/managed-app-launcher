namespace ManagedLauncher.Core.Config;

/// <summary>How the launcher behaves while it shows one list.</summary>
public sealed class ListOptions
{
    /// <summary>After an app press launches or focuses something, leave the launcher (Back, or the first page).</summary>
    public bool ReturnAfterLaunch { get; set; }

    public string? Icon { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => !ReturnAfterLaunch && Icon is null;

    public ListOptions Clone() => new() { ReturnAfterLaunch = ReturnAfterLaunch, Icon = Icon };
}
