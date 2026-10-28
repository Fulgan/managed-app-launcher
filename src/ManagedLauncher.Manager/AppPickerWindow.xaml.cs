using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Manager;

/// <summary>Searchable multi-select list of apps to add, filled from the Start menu or from running apps.</summary>
public partial class AppPickerWindow : Window
{
    private readonly List<Choice> all;

    /// <summary>Set once the window closes; icon jobs still queued on the shared STA worker then return at once.</summary>
    private volatile bool closed;

    private AppPickerWindow(string title, string heading, List<Choice> choices)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        all = choices;
        AppsBox.ItemsSource = all;
        CountText.Text = $"{all.Count} apps";
        Closed += (_, _) => closed = true;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            foreach (var choice in all)
                LoadIcon(choice);
        };
    }

    public IReadOnlyList<Choice> Chosen { get; private set; } = [];

    public static AppPickerWindow StartMenu() => new("Add from Start menu", "Start menu", Choices(AppCatalog.StartMenu()));

    public static AppPickerWindow SteamGames() => new("Add Steam game", "Steam games", Choices(AppCatalog.Steam()));

    public static AppPickerWindow Running() => new("Add running app", "Running apps", Choices(AppCatalog.Running(Environment.ProcessId)));

    private static List<Choice> Choices(IReadOnlyList<AppChoice> apps) => apps.Select(a => new Choice(a)).ToList();

    private void LoadIcon(Choice choice)
    {
        var source = TargetKindDetector.Detect(choice.Target) == Core.Config.TargetKind.Aumid
            ? TargetResolver.AppsFolderPrefix + choice.Target
            : choice.Target;
        App.Shell.RunAsync(() =>
        {
            if (closed)
                return null;
            using var bitmap = choice.IconFile is { } file ? IconExtractor.LoadImageFile(file) : IconExtractor.Extract(source, 32);
            if (bitmap is null)
                return null;
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(stream.ToArray());
            image.EndInit();
            image.Freeze();
            return (ImageSource)image;
        }).ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) choice.Icon = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || c.Detail.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        AppsBox.ItemsSource = shown;
        CountText.Text = $"{shown.Count} apps";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Finish();

    private void AppsBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Finish();

    private void Finish()
    {
        Chosen = AppsBox.SelectedItems.OfType<Choice>().ToList();
        DialogResult = Chosen.Count > 0;
    }

    public sealed class Choice(AppChoice app) : INotifyPropertyChanged
    {
        private ImageSource? icon;

        public string Name { get; } = app.Name;
        public string Target { get; } = app.Entry.Target;
        public string Detail { get; } = app.Detail;
        public PinAdvisor.PinPlan Plan { get; } = app.Plan;

        /// <summary>Image to show as the choice's icon instead of the target's shell icon.</summary>
        public string? IconFile { get; } = app.Entry.Icon;

        public ImageSource? Icon
        {
            get => icon;
            set { icon = value; Changed(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
