using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Manager;

/// <summary>One row of the app list: the item plus what the deck would show for it.</summary>
public sealed class AppItem(Entry entry, AppConfig config) : INotifyPropertyChanged
{
    private ImageSource? icon;
    private bool isHidden;

    /// <summary>For an auto-folder row: the item as its source gives it, before overrides; null otherwise.</summary>
    public Entry? Generated { get; init; }

    /// <summary>An auto-folder item left out of the deck.</summary>
    public bool IsHidden
    {
        get => isHidden;
        set { isHidden = value; Changed(); Changed(nameof(Target)); }
    }

    public Entry Entry { get; } = entry;

    public ImageSource? Icon
    {
        get => icon;
        set { icon = value; Changed(); }
    }

    public bool IsBreak => Entry.IsBreak;

    /// <summary>A launchable app, as opposed to a break, folder or link; only apps have launch details.</summary>
    public bool IsApp => Entry.IsApp;

    /// <summary>Segoe Fluent glyph shown in place of an icon for a break.</summary>
    public string? BreakGlyph => Entry.Break switch
    {
        ListBreak.Row => "",
        ListBreak.Page => "",
        _ => null,
    };

    public string Name => Entry.Break switch
    {
        ListBreak.Row => "Row break",
        ListBreak.Page => "Page break",
        _ when Entry.Opens is not null => config.TitleOf(Entry),
        _ => Entry.Title ?? AppNames.Default(Entry),
    };

    public string Target => Entry.Break switch
    {
        ListBreak.Row => "the next app starts on a new row",
        ListBreak.Page => "the next app starts on a new page",
        _ when IsHidden => "hidden on the deck",
        _ when Entry.IsFolder && Generated is not null => "folder",
        _ when Entry.IsFolder && config.SourceOf(Entry.Folder) is { } source => $"auto folder · {AutoFolders.Describe(source)}",
        _ when Entry.IsFolder => FolderDetail(),
        _ when Entry.IsLink => $"opens {config.PathOf(Entry.Link)}",
        _ => Entry.Target,
    };

    private string FolderDetail()
    {
        var (apps, folders) = config.ContentsOf(Entry.Folder);
        return folders == 0 ? $"folder · {Count(apps, "app")}" : $"folder · {Count(apps, "app")}, {Count(folders, "folder")}";
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    public string? Problem => IsBreak ? null : Entry.Opens is not null ? Entry.LinkProblem : TargetResolver.Resolve(Entry).Error;

    public bool HasProblem => Problem is not null;

    /// <summary>Re-reads every derived property after the entry was edited.</summary>
    public void Refresh()
    {
        Changed(nameof(Name));
        Changed(nameof(Target));
        Changed(nameof(Problem));
        Changed(nameof(HasProblem));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
