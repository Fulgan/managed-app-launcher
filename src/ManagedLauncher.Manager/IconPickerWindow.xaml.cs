using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ManagedLauncher.Core.Icons;
using Microsoft.Win32;

namespace ManagedLauncher.Manager;

/// <summary>
/// Picks a custom key icon: one icon out of an exe, dll or .ico, or a whole image file. The result is an
/// <see cref="Core.Config.Entry.Icon"/> value (<c>file,index</c> or an image path), or null for the app's own icon.
/// </summary>
public partial class IconPickerWindow : Window
{
    private const int TileSize = 96;
    private static readonly string WindowsIcons = Path.Combine(Environment.SystemDirectory, "imageres.dll");
    private static readonly HashSet<string> IconFiles = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".ico", ".icl", ".cpl", ".mun" };

    private readonly string? appFile;
    private string? file;

    /// <summary>Closes with true when a choice was made; <see cref="Chosen"/> null then means "use the app's own icon".</summary>
    public string? Chosen { get; private set; }

    /// <param name="current">The entry's current custom icon, if any.</param>
    /// <param name="appFile">The app's program file, offered as the first source.</param>
    public IconPickerWindow(string? current, string? appFile)
    {
        InitializeComponent();
        this.appFile = appFile is not null && File.Exists(appFile) ? appFile : null;
        AppButton.IsEnabled = this.appFile is not null;
        if (current is not null)
        {
            var (path, index) = IconExtractor.ParseIconRef(current);
            Show(path, index ?? 0);
        }
        else
        {
            Show(this.appFile ?? WindowsIcons, 0);
        }
    }

    private void App_Click(object sender, RoutedEventArgs e) => Show(appFile!, 0);

    private void Windows_Click(object sender, RoutedEventArgs e) => Show(WindowsIcons, 0);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Icon source",
            Filter = "Icons and images|*.exe;*.dll;*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            DereferenceLinks = true,
        };
        if (file is not null && File.Exists(file))
            dialog.InitialDirectory = Path.GetDirectoryName(file);
        if (dialog.ShowDialog(this) == true)
            Show(dialog.FileName, 0);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Chosen = null;
        DialogResult = true;
    }

    private void Use_Click(object sender, RoutedEventArgs e) => Finish();

    private void IconsBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Finish();

    private void Finish()
    {
        if (IconsBox.SelectedItem is not Tile tile || file is null)
            return;
        Chosen = tile.Index is { } index ? $"{file},{index}" : file;
        DialogResult = true;
    }

    /// <summary>Lists every icon of an icon-bearing file, or the image itself; tiles fill in as they are extracted.</summary>
    private void Show(string path, int select)
    {
        file = path;
        SourceText.Text = path;
        var tiles = new ObservableCollection<Tile>();
        IconsBox.ItemsSource = tiles;
        var isIconFile = IconFiles.Contains(Path.GetExtension(path));
        var count = isIconFile ? IconExtractor.CountIcons(path) : File.Exists(path) ? 1 : 0;
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < count; i++)
        {
            var tile = new Tile(isIconFile ? i : null);
            tiles.Add(tile);
            var index = i;
            App.Shell.RunAsync(() => ToImage(isIconFile ? IconExtractor.ExtractIndexed(path, index, TileSize) : IconExtractor.LoadImageFile(path)))
                .ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) tile.Image = t.Result; },
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (select >= 0 && select < tiles.Count)
        {
            IconsBox.SelectedIndex = select;
            IconsBox.ScrollIntoView(tiles[select]);
        }
    }

    private static ImageSource? ToImage(System.Drawing.Bitmap? bitmap)
    {
        if (bitmap is null)
            return null;
        using (bitmap)
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return KeyImages.ToImage(stream.ToArray());
        }
    }

    public sealed class Tile(int? index) : INotifyPropertyChanged
    {
        private ImageSource? image;

        /// <summary>Icon index in the file; null when the file is an image.</summary>
        public int? Index { get; } = index;

        public ImageSource? Image
        {
            get => image;
            set { image = value; Changed(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
