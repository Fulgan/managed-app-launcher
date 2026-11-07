using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ManagedLauncher.Manager;

/// <summary>
/// One list in the sidebar tree: a top-level list or a folder, indented by its depth. While <see cref="IsEditing"/>
/// its name is edited in place as <see cref="Draft"/>.
/// </summary>
public sealed class ListRow(string id, string name, int depth, bool hasChildren, bool isExpanded, bool isAuto = false) : INotifyPropertyChanged
{
    private bool isEditing;
    private string draft = name;
    private string? error;
    private bool isDropTarget;

    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>1 for a top-level list, 2 for a folder in it, and so on.</summary>
    public int Depth { get; } = depth;

    public bool IsFolder => Depth > 1;

    public Thickness Indent => new((Depth - 1) * 14, 0, 0, 0);

    public bool HasChildren { get; } = hasChildren;

    public bool IsExpanded { get; } = isExpanded;

    /// <summary>Segoe Fluent chevron: right when collapsed, down when expanded.</summary>
    public string Chevron => IsExpanded ? "" : "";

    /// <summary>An auto-folder: its items come from a source, nothing can be dropped into it.</summary>
    public bool IsAuto { get; } = isAuto;

    public string Glyph => IsAuto ? "" : IsFolder ? "" : "";

    public bool IsEditing
    {
        get => isEditing;
        set { isEditing = value; Changed(); }
    }

    public string Draft
    {
        get => draft;
        set { draft = value; Changed(); }
    }

    /// <summary>Why <see cref="Draft"/> can't be used, shown while typing; null when it can.</summary>
    public string? Error
    {
        get => error;
        set { error = value; Changed(); Changed(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    /// <summary>An item is being dragged over this list and would land in it.</summary>
    public bool IsDropTarget
    {
        get => isDropTarget;
        set { isDropTarget = value; Changed(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
