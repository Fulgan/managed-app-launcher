using System.Text.Json.Serialization;

namespace ManagedLauncher.Core.Config;

/// <summary>One list of items: a top-level list or the contents of a folder.</summary>
public sealed class StoredList
{
    public ListKind Kind { get; set; } = ListKind.Static;

    /// <summary>
    /// The list's name. A top-level list is named by its key in <c>lists</c>, so only folders store it here.
    /// </summary>
    [JsonIgnore]
    public string Name { get; set; } = "";

    [JsonInclude, JsonPropertyName("name")]
    internal string? StoredName
    {
        get => IsTopLevel ? null : Name;
        set => Name = value ?? "";
    }

    /// <summary>Set by <see cref="AppConfig.Normalize"/>; decides whether the name is written here.</summary>
    [JsonIgnore]
    internal bool IsTopLevel { get; set; }

    public List<Entry> Items { get; set; } = [];

    /// <summary>Where a dynamic list takes its items from; null for static lists.</summary>
    public ListSource? Source { get; set; }

    /// <summary>Dynamic lists: targets and directories left out of the generated items.</summary>
    public List<string>? Hidden { get; set; }

    /// <summary>Null when every option is at its default.</summary>
    public ListOptions? Options { get; set; }
}

/// <summary>What a dynamic list (auto-folder) is built from.</summary>
public sealed class ListSource
{
    public SourceType Type { get; set; }

    /// <summary>Start directory of a directory source; environment variables are expanded.</summary>
    public string? Path { get; set; }

    /// <summary>File types a directory source takes: exe, lnk, symlink, bat (scripts: .bat, .cmd, .ps1); null or empty means all.</summary>
    public List<string>? Types { get; set; }
}
