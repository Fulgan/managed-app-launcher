using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Manager;

/// <summary>One row of the app list: the entry plus what the deck would show for it.</summary>
public sealed class AppItem(Entry entry) : INotifyPropertyChanged
{
    private ImageSource? icon;

    public Entry Entry { get; } = entry;

    public ImageSource? Icon
    {
        get => icon;
        set { icon = value; Changed(); }
    }

    public bool IsBreak => Entry.IsBreak;

    /// <summary>Segoe Fluent glyph shown in place of an icon for a break.</summary>
    public string? BreakGlyph => Entry.Break switch
    {
        ListBreak.Row => "\uE751",
        ListBreak.Page => "\uE7C3",
        _ => null,
    };

    public string Name => Entry.Break switch
    {
        ListBreak.Row => "Row break",
        ListBreak.Page => "Page break",
        _ => Entry.Title ?? DefaultName(Entry),
    };

    public string Target => Entry.Break switch
    {
        ListBreak.Row => "the next app starts on a new row",
        ListBreak.Page => "the next app starts on a new page",
        _ => Entry.Target,
    };

    public string? Problem => IsBreak ? null : TargetResolver.Resolve(Entry).Error;

    public bool HasProblem => Problem is not null;

    /// <summary>Re-reads every derived property after the entry was edited.</summary>
    public void Refresh()
    {
        Changed(nameof(Name));
        Changed(nameof(Target));
        Changed(nameof(Problem));
        Changed(nameof(HasProblem));
    }

    public static string DefaultName(Entry entry)
    {
        var target = entry.Target.Trim('"');
        if (target.StartsWith(TargetResolver.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            target = target[TargetResolver.AppsFolderPrefix.Length..];
        if (TargetKindDetector.Detect(target) == TargetKind.Aumid)
            return Core.Icons.ShellItems.GetDisplayName(TargetResolver.AppsFolderPrefix + target) ?? target;
        return Path.GetFileNameWithoutExtension(target);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
