using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
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

    public IReadOnlyList<string> Chosen { get; private set; } = [];

    /// <summary>
    /// Start menu shortcuts and packaged (Store) apps. Shortcuts are offered as <c>.lnk</c> paths because installers
    /// keep them current across updates; packaged apps have no shortcut file and are offered by AUMID.
    /// </summary>
    public static AppPickerWindow StartMenu() =>
        new("Add from Start menu", "Start menu", Shortcuts().Concat(PackagedApps())
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>Apps showing a window right now, offered by exe path, or by AUMID when packaged.</summary>
    public static AppPickerWindow Running()
    {
        var names = ShellItems.EnumerateApps()
            .GroupBy(a => a.Aumid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var apps = RunningApps.Collect(WindowFinder.VisibleWindows(), new Win32ProcessSource().Snapshot(), names,
            Environment.ProcessId, Describe);
        return new("Add running app", "Running apps", apps.Select(a => new Choice(a.Name, a.Target, a.Detail)).ToList());
    }

    private static string? Describe(string exe)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<Choice> Shortcuts()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ];
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return new Choice(name, file, Path.GetRelativePath(root, Path.GetDirectoryName(file)!));
            }
        }
    }

    private static IEnumerable<Choice> PackagedApps() =>
        ShellItems.EnumerateApps()
            .Where(a => a.Aumid.Contains('!'))
            .Select(a => new Choice(a.Name, a.Aumid, "Store app"));

    private void LoadIcon(Choice choice)
    {
        var source = TargetKindDetector.Detect(choice.Target) == Core.Config.TargetKind.Aumid
            ? TargetResolver.AppsFolderPrefix + choice.Target
            : choice.Target;
        App.Shell.RunAsync(() =>
        {
            if (closed)
                return null;
            using var bitmap = IconExtractor.Extract(source, 32);
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
        Chosen = AppsBox.SelectedItems.OfType<Choice>().Select(c => c.Target).ToList();
        DialogResult = Chosen.Count > 0;
    }

    public sealed class Choice(string name, string target, string detail) : INotifyPropertyChanged
    {
        private ImageSource? icon;

        public string Name { get; } = name;
        public string Target { get; } = target;
        public string Detail { get; } = detail;

        public ImageSource? Icon
        {
            get => icon;
            set { icon = value; Changed(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
