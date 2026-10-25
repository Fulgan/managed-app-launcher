using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ManagedLauncher.Manager;

/// <summary>One list in the sidebar; while <see cref="IsEditing"/> its name is edited in place as <see cref="Draft"/>.</summary>
public sealed class ListRow(string name) : INotifyPropertyChanged
{
    private bool isEditing;
    private string draft = name;
    private string? error;

    public string Name { get; } = name;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
