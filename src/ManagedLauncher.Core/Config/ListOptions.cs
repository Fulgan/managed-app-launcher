namespace ManagedLauncher.Core.Config;

/// <summary>How the launcher behaves while it shows one list.</summary>
public sealed class ListOptions
{
    /// <summary>After an app press launches or focuses something, leave the launcher (Back, or the first page).</summary>
    public bool ReturnAfterLaunch { get; set; }
}
