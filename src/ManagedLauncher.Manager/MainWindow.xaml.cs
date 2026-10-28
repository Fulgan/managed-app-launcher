using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedLauncher.Core;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Paging;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;
using Microsoft.Win32;

namespace ManagedLauncher.Manager;

/// <summary>
/// Edits the app lists in <c>config.json</c>. Every change is written at once through <see cref="ConfigStore.Update"/>
/// so the plugin redraws the deck live; edits made elsewhere (CLI, Explorer verb) are picked up the same way.
/// </summary>
public partial class MainWindow : Window
{
    private const string AppDragFormat = "ManagedLauncher.AppItem";

    private readonly string path = AppPaths.ConfigFile;
    private readonly DispatcherTimer reloadTimer;
    private readonly FileSystemWatcher watcher;
    private AppConfig config = new AppConfig().Normalize();
    private ObservableCollection<AppItem> items = [];
    private string currentList = AppConfig.DefaultListName;

    /// <summary>The config as last loaded or saved here; a file change that matches it is this window's own write.</summary>
    private string knownConfig = "";

    /// <summary>The current list as the window last loaded or saved it; saving refuses when the file no longer matches.</summary>
    private string listBaseline = "";
    private bool filling;
    private Point dragStart;
    private bool dragArmed;

    public MainWindow()
    {
        InitializeComponent();
        FillCombo(KindBox, ("", "detect from target"), ("lnk", "shortcut"), ("exe", "exe"), ("glob", "versioned path"), ("aumid", "app id (AUMID)"), ("url", "link (URL)"));
        FillCombo(OnRunningBox, ("", "default"), ("auto", "focus, else relaunch"), ("focus", "focus"), ("relaunch", "relaunch"), ("none", "do nothing"));
        FillCombo(LongPressBox, ("", "default"), ("auto", "close app"), ("none", "same as short press"));
        FillCombo(KillModeBox, ("", "default"), ("all", "all instances"), ("newest", "newest instance"));
        FillCombo(AdminBox, ("", "default"), ("true", "always"), ("false", "never"));
        FillCombo(IndicatorBox, ("", "default"), ("dot", "dot"), ("border", "border"));
        ShowInherited();
        StartTestPoll();
        SteamMenuItem.Visibility = SteamLibrary.InstallPath() is null ? Visibility.Collapsed : Visibility.Visible;

        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        reloadTimer.Tick += (_, _) => ReloadIfChanged();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => { reloadTimer.Stop(); reloadTimer.Start(); });
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { reloadTimer.Stop(); reloadTimer.Start(); });
        watcher.EnableRaisingEvents = true;

        Reload(keepSelection: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        watcher.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Picks up changes made by other programs. Waits while a list name is being edited or an alert is open, so an
    /// outside change never commits a half-typed name or shifts the item an alert is about.
    /// </summary>
    private void ReloadIfChanged()
    {
        if (alert is { Task.IsCompleted: false } || ListsBox.Items.OfType<ListRow>().Any(r => r.IsEditing))
            return;
        reloadTimer.Stop();
        if (!ConfigStore.TryLoad(path, out var loaded, out var error))
        {
            StatusText.Text = $"config.json is invalid, not reloaded: {error}";
            return;
        }
        if (Fingerprint(loaded) != knownConfig)
            Reload(keepSelection: true, loaded);
    }

    private static string Fingerprint(AppConfig c) => System.Text.Json.JsonSerializer.Serialize(c, ConfigStore.JsonOptions);

    private static string Fingerprint(IReadOnlyList<Entry> list) => System.Text.Json.JsonSerializer.Serialize(list, ConfigStore.JsonOptions);

    private void Reload(bool keepSelection, AppConfig? loaded = null)
    {
        if (loaded is null && !ConfigStore.TryLoad(path, out loaded, out var error))
        {
            StatusText.Text = $"config.json is invalid, not reloaded: {error}";
            return;
        }
        config = loaded;
        knownConfig = Fingerprint(config);
        var selected = keepSelection ? AppsBox.SelectedIndex : -1;
        if (!config.Lists.ContainsKey(currentList))
            currentList = config.HomeList;

        filling = true;
        ListsBox.ItemsSource = ListNames().Select(name => new ListRow(name)).ToList();
        ListsBox.SelectedItem = ListsBox.Items.Cast<ListRow>().FirstOrDefault(r => string.Equals(r.Name, currentList, StringComparison.OrdinalIgnoreCase));
        ShowDecks();
        filling = false;

        ShowList(selected);
    }

    /// <summary>Home list first, then the others by name.</summary>
    private IEnumerable<string> ListNames() => config.Lists.Keys
        .OrderBy(k => !string.Equals(k, config.HomeList, StringComparison.OrdinalIgnoreCase))
        .ThenBy(k => k, StringComparer.OrdinalIgnoreCase);

    private ListRow? SelectedRow => ListsBox.SelectedItem as ListRow;

    /// <summary>One row per deck the plugin has seen: its name and the list it is dedicated to, if any.</summary>
    private void ShowDecks()
    {
        var decks = KnownDecks.Load(AppPaths.DevicesFile);
        DecksSection.Visibility = decks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DecksPanel.Children.Clear();
        foreach (var deck in decks.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var box = new ComboBox { Tag = deck.Id, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
            box.Items.Add(new ComboBoxItem { Content = "No fixed list", Tag = "" });
            foreach (var list in ListNames())
                box.Items.Add(new ComboBoxItem { Content = list, Tag = list });
            var current = config.Decks.GetValueOrDefault(deck.Id) ?? "";
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, current, StringComparison.OrdinalIgnoreCase));
            box.SelectionChanged += Deck_Changed;
            DecksPanel.Children.Add(new TextBlock
            {
                Text = $"{deck.Name}  ·  {deck.Columns}×{deck.Rows}", FontSize = 13, Margin = new Thickness(0, 4, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            DecksPanel.Children.Add(box);
        }
    }

    private void ReturnAfterLaunch_Changed(object sender, RoutedEventArgs e)
    {
        if (filling)
            return;
        var list = currentList;
        var on = ReturnAfterLaunchBox.IsChecked == true;
        TryUpdate(c => c.UpdateOptions(list, o => o.ReturnAfterLaunch = on));
    }

    private void ListIcon_Click(object sender, RoutedEventArgs e)
    {
        var list = currentList;
        var current = config.OptionsOf(list).Icon;
        var picker = new IconPickerWindow(current, null, "Reset to default", config.GetList(list)) { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen == current)
            return;
        if (TryUpdate(c => c.UpdateOptions(list, o => o.Icon = picker.Chosen)))
            LoadOpenKeyPreview();
    }

    private void LoadOpenKeyPreview()
    {
        var list = currentList;
        var icon = config.OptionsOf(list).Icon;
        App.Shell.RunAsync(() => KeyImages.RenderOpen(list, icon)).ContinueWith(
            t => { if (t.Status == TaskStatus.RanToCompletion && currentList == list) OpenKeyPreview.Source = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Deck_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (filling || sender is not ComboBox { Tag: string id, SelectedItem: ComboBoxItem { Tag: string list } })
            return;
        TryUpdate(c =>
        {
            if (list.Length == 0)
                c.Decks.Remove(id);
            else
                c.Decks[id] = list;
        });
    }

    private void ShowList(int select = -1)
    {
        ListTitle.Text = currentList;
        ListTitle.ToolTip = string.Equals(currentList, config.HomeList, StringComparison.OrdinalIgnoreCase)
            ? "Home list: shown by keys and decks that don't name a list"
            : null;
        var wasFilling = filling;
        filling = true;
        ReturnAfterLaunchBox.IsChecked = config.OptionsOf(currentList).ReturnAfterLaunch;
        filling = wasFilling;
        LoadOpenKeyPreview();
        items = new ObservableCollection<AppItem>(config.GetList(currentList).Select(e => new AppItem(e)));
        listBaseline = Fingerprint(config.GetList(currentList));
        AppsBox.ItemsSource = items;
        if (select >= 0 && select < items.Count)
            AppsBox.SelectedIndex = select;
        UpdatePages();
        foreach (var item in items)
            LoadIcon(item);
        ShowDetails();
    }

    private void LoadIcon(AppItem item)
    {
        if (item.IsBreak)
            return;
        var entry = item.Entry;
        var snapshot = config;
        App.Shell.RunAsync(() => KeyImages.Render(entry, snapshot)).ContinueWith(
            t => { if (t.Status == TaskStatus.RanToCompletion) item.Icon = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdatePages()
    {
        var kinds = items.Select(i => i.Entry.LayoutKind).ToList();
        var apps = items.Count(i => !i.IsBreak);
        var pages = LauncherProfiles.All
            .GroupBy(p => (p.Columns, p.Rows))
            .Select(g => $"{string.Join(" / ", g.Select(p => p.Label))}: {new LauncherLayout(g.Key.Columns * g.Key.Rows, g.Key.Columns, kinds).PageCount}");
        PagesText.Text = $"{apps} apps · pages on {string.Join(" · ", pages)}";
    }

    /// <summary>Raised inside an update when the file no longer holds the list the window is editing.</summary>
    private sealed class ListChangedElsewhere : Exception;

    /// <summary>
    /// Writes the current list into the latest file content, so concurrent edits to other lists survive. Refuses when
    /// the list itself changed (or was renamed or removed) since the window loaded it, and reloads instead: the
    /// window's copy is stale and writing it would undo that change.
    /// </summary>
    private void Save()
    {
        var entries = items.Select(i => i.Entry).ToList();
        var list = currentList;
        var baseline = listBaseline;
        if (TryUpdate(c =>
            {
                if (!c.Lists.TryGetValue(list, out var current) || Fingerprint(current) != baseline)
                    throw new ListChangedElsewhere();
                c.Lists[list] = entries;
            }))
        {
            listBaseline = Fingerprint(entries);
            UpdatePages();
        }
    }

    /// <summary>Applies one change through the shared lock and keeps the window's copy in step; reports failure in the status bar.</summary>
    private bool TryUpdate(Action<AppConfig> mutate)
    {
        try
        {
            config = ConfigStore.Update(path, mutate);
            knownConfig = Fingerprint(config);
            return true;
        }
        catch (ListChangedElsewhere)
        {
            Reload(keepSelection: true);
            StatusText.Text = "This list was changed by another program and has been reloaded. Please redo your last change.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"could not save: {ex.Message}";
        }
        return false;
    }

    private AppItem? Selected => AppsBox.SelectedItem as AppItem;

    // Alerts

    private enum ChoiceKind { Cancel, Default, Destructive }

    private TaskCompletionSource<int>? alert;
    private int alertCancel = -1;

    /// <summary>Asks to confirm a destructive action; true when confirmed.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string confirm) =>
        await AskAsync(title, message, ("Cancel", ChoiceKind.Cancel), (confirm, ChoiceKind.Destructive)) == 1;

    /// <summary>A single-button notice, for errors.</summary>
    public Task ShowErrorAsync(string title, string message) => AskAsync(title, message, ("OK", ChoiceKind.Default));

    /// <summary>
    /// Asks inside the window instead of a system message box: a rounded button per choice, side by side up to
    /// three, stacked beyond. Resolves to the chosen index; Esc picks the Cancel choice (or -1 when there is none),
    /// Enter the focused one, which starts as the last. Another alert replacing this one resolves it to -1.
    /// </summary>
    private Task<int> AskAsync(string title, string message, params (string Text, ChoiceKind Kind)[] choices)
    {
        alert?.TrySetResult(-1);
        var pending = alert = new TaskCompletionSource<int>();
        AlertTitle.Text = title;
        AlertMessage.Text = message;
        AlertMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        alertCancel = Array.FindIndex(choices, c => c.Kind == ChoiceKind.Cancel);

        AlertButtons.Children.Clear();
        AlertButtons.Rows = choices.Length <= 3 ? 1 : 0;
        AlertButtons.Columns = choices.Length <= 3 ? choices.Length : 1;
        for (var i = 0; i < choices.Length; i++)
        {
            var (text, kind) = choices[i];
            var index = i;
            var button = new Button
            {
                Style = (Style)FindResource("AlertButton"),
                Background = kind switch
                {
                    ChoiceKind.Destructive => (Brush)FindResource("Error"),
                    ChoiceKind.Default => (Brush)FindResource("Accent"),
                    _ => (Brush)FindResource("Pressed"),
                },
                Content = new TextBlock { Text = text, Foreground = Brushes.White },
            };
            button.Click += (_, _) => pending.TrySetResult(index);
            AlertButtons.Children.Add(button);
        }

        var restoreFocus = Keyboard.FocusedElement as IInputElement;
        AlertLayer.Visibility = Visibility.Visible;
        var last = AlertButtons.Children[^1];
        Dispatcher.BeginInvoke(() => last.Focus(), DispatcherPriority.Input);
        return pending.Task.ContinueWith(t =>
        {
            if (alert == pending)
            {
                AlertLayer.Visibility = Visibility.Collapsed;
                restoreFocus?.Focus();
            }
            return t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AlertLayer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            alert?.TrySetResult(alertCancel);
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is Button focused && focused.Parent == AlertButtons)
            alert?.TrySetResult(AlertButtons.Children.IndexOf(focused));
        else
            return;
        e.Handled = true;
    }

    // Lists

    private void ListsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var row in e.RemovedItems.OfType<ListRow>().Where(r => r.IsEditing))
            CommitRename(row);
        if (filling || SelectedRow is not { } selected)
            return;
        StatusText.Text = "";
        currentList = selected.Name;
        ShowList();
    }

    private void AddList_Click(object sender, RoutedEventArgs e)
    {
        string? name = null;
        if (!TryUpdate(c => c.AddList(name = c.UnusedListName("New list"))))
            return;
        currentList = name!;
        Reload(keepSelection: false);
        BeginRename();
    }

    private void DuplicateList_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Name is not { } source)
            return;
        string? name = null;
        if (!TryUpdate(c => name = c.CopyList(source)))
            return;
        currentList = name!;
        Reload(keepSelection: false);
        BeginRename();
    }

    private void RenameList_Click(object sender, RoutedEventArgs e) => BeginRename();

    private void ListsBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(ListsBox, source) is null)
            return;
        BeginRename();
    }

    private void ListsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
            BeginRename();
    }

    /// <summary>Swaps the selected list's name for an editor with the name selected; typing replaces it.</summary>
    private void BeginRename()
    {
        if (SelectedRow is not { IsEditing: false } row)
            return;
        row.Draft = row.Name;
        row.Error = null;
        row.IsEditing = true;
    }

    private void ListName_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Input);
    }

    private void ListName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ListRow { IsEditing: true } row })
            row.Error = config.CheckListName(row.Name, row.Draft);
    }

    private void ListName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ListRow row })
            return;
        if (e.Key == Key.Escape)
        {
            row.IsEditing = false;
            ListsBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (row.Error is { } error)
            {
                StatusText.Text = error;
                return;
            }
            CommitRename(row);
        }
    }

    private void ListName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ListRow { IsEditing: true } row })
            CommitRename(row);
    }

    /// <summary>Applies the edited name; an invalid one is dropped with the reason in the status bar.</summary>
    private void CommitRename(ListRow row)
    {
        row.IsEditing = false;
        var from = row.Name;
        var name = row.Draft.Trim();
        if (name == from)
            return;
        var error = config.CheckListName(from, name);
        if (error is null && !TryUpdate(c => error = c.RenameList(from, name)))
            return;
        if (error is not null)
        {
            StatusText.Text = $"Name not changed. {error}";
            return;
        }
        if (string.Equals(currentList, from, StringComparison.OrdinalIgnoreCase))
            currentList = name;
        Dispatcher.BeginInvoke(() => Reload(keepSelection: true), DispatcherPriority.Background);
    }

    private async void RemoveList_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Name is not { } name)
            return;
        if (string.Equals(name, config.HomeList, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = $"'{name}' is the home list, shown when a key or deck names no list. It can't be removed.";
            return;
        }
        var count = config.GetList(name).Count;
        if (!await ConfirmAsync($"Remove list “{name}”?",
                $"Its {count} app{(count == 1 ? "" : "s")} will be removed too. Keys set to this list go blank until you pick another list.", "Remove"))
            return;
        if (!TryUpdate(c => c.Lists.Remove(name)))
            return;
        currentList = config.HomeList;
        Reload(keepSelection: false);
    }

    // Adding apps

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add apps",
            Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*",
            Multiselect = true,
            DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        if (dialog.ShowDialog(this) == true)
            AddPaths(dialog.FileNames, InsertIndex());
    }

    private void AddStartMenu_Click(object sender, RoutedEventArgs e)
    {
        var picker = AppPickerWindow.StartMenu();
        picker.Owner = this;
        if (picker.ShowDialog() == true)
            AddChoices(picker.Chosen, InsertIndex());
    }

    private void AddRunning_Click(object sender, RoutedEventArgs e)
    {
        var picker = AppPickerWindow.Running();
        picker.Owner = this;
        if (picker.ShowDialog() == true)
            AddChoices(picker.Chosen, InsertIndex());
    }

    private void AddSteam_Click(object sender, RoutedEventArgs e)
    {
        var picker = AppPickerWindow.SteamGames();
        picker.Owner = this;
        if (picker.ShowDialog() == true)
            AddChoices(picker.Chosen, InsertIndex());
    }

    /// <summary>Opens a button's choices as a menu above it.</summary>
    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AddRowBreak_Click(object sender, RoutedEventArgs e) => AddBreak(ListBreak.Row);

    private void AddPageBreak_Click(object sender, RoutedEventArgs e) => AddBreak(ListBreak.Page);

    private void AddBreak(ListBreak kind)
    {
        var at = InsertIndex();
        var item = new AppItem(new Entry { Break = kind });
        items.Insert(at, item);
        Save();
        RefreshIndexes();
        AppsBox.SelectedIndex = at;
        AppsBox.ScrollIntoView(item);
    }

    private int InsertIndex() => AppsBox.SelectedIndex >= 0 ? AppsBox.SelectedIndex + 1 : items.Count;

    private void AddChoices(IEnumerable<AppPickerWindow.Choice> choices, int at) =>
        AddPlans(choices.Select(c => c.Plan), at);

    private void AddPaths(IEnumerable<string> paths, int at) => AddPlans(paths.Select(p => PinAdvisor.Plan(p, null)), at);

    private void AddPlans(IEnumerable<PinAdvisor.PinPlan> plans, int at)
    {
        var advice = new List<string>();
        var index = Math.Clamp(at, 0, items.Count);
        var first = index;
        foreach (var plan in plans)
        {
            var item = new AppItem(plan.Entry);
            items.Insert(index++, item);
            LoadIcon(item);
            advice.AddRange(plan.Advice.Select(a => $"{item.Name}: {a}"));
        }
        if (index == first)
            return;
        Save();
        RefreshIndexes();
        AppsBox.SelectedIndex = first;
        AppsBox.ScrollIntoView(items[index - 1]);
        AppsBox.ScrollIntoView(items[first]);
        StatusText.Text = string.Join("   ", advice);
    }

    // Removing and ordering

    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private async void AppsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
            await RemoveSelectedAsync();
    }

    private async Task RemoveSelectedAsync()
    {
        if (Selected is not { } item)
            return;
        if (!await ConfirmAsync($"Remove “{item.Name}”?",
                $"It leaves the list “{currentList}” and its key on the deck.", "Remove"))
            return;
        var index = items.IndexOf(item);
        if (index < 0)
            return;
        items.RemoveAt(index);
        Save();
        RefreshIndexes();
        AppsBox.SelectedIndex = Math.Min(index, items.Count - 1);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var index = AppsBox.SelectedIndex;
        var to = index + delta;
        if (index < 0 || to < 0 || to >= items.Count)
            return;
        items.Move(index, to);
        Save();
        RefreshIndexes();
        AppsBox.SelectedIndex = to;
    }

    /// <summary>Position numbers are computed per container, so containers are rebuilt after the order changes.</summary>
    private void RefreshIndexes()
    {
        var selected = AppsBox.SelectedIndex;
        AppsBox.Items.Refresh();
        AppsBox.SelectedIndex = selected;
    }

    // Drag and drop: files from Explorer are added, rows dragged within the list are moved.

    /// <summary>
    /// A drag may only start from a press on an app row of this list. A press that began elsewhere, such as the
    /// double-click that closes a picker or file dialog over the list, must not turn into a drag.
    /// </summary>
    private void AppsBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(AppsBox);
        dragArmed = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AppsBox, source) is ListBoxItem;
    }

    private void AppsBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => dragArmed = false;

    private void AppsBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragArmed || e.LeftButton != MouseButtonState.Pressed || Selected is not { } item)
            return;
        var delta = e.GetPosition(AppsBox) - dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        dragArmed = false;
        DragDrop.DoDragDrop(AppsBox, new DataObject(AppDragFormat, item), DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void AppsBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(AppDragFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        ShowDropLine(e);
        e.Handled = true;
    }

    private void AppsBox_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Draws the insertion line between the rows where the drop would land; hidden where dropping changes nothing
    /// (on or right below the dragged row itself).
    /// </summary>
    private void ShowDropLine(DragEventArgs e)
    {
        var at = DropIndex(e);
        var from = e.Data.GetData(AppDragFormat) is AppItem dragged ? items.IndexOf(dragged) : -1;
        var row = items.Count == 0 ? null : AppsBox.ItemContainerGenerator.ContainerFromIndex(Math.Min(at, items.Count - 1)) as ListBoxItem;
        if (e.Effects == DragDropEffects.None || row is null || (from >= 0 && (at == from || at == from + 1)))
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        var origin = row.TranslatePoint(new Point(0, at < items.Count ? 0 : row.ActualHeight), (UIElement)DropLine.Parent);
        Canvas.SetLeft(DropLine, origin.X + 8);
        Canvas.SetTop(DropLine, origin.Y - DropLine.Height / 2);
        DropLine.Width = Math.Max(0, row.ActualWidth - 16);
        DropLine.Visibility = Visibility.Visible;
    }

    private void AppsBox_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        var at = DropIndex(e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddPaths(files, at);
        }
        else if (e.Data.GetData(AppDragFormat) is AppItem dragged)
        {
            var from = items.IndexOf(dragged);
            var to = Math.Clamp(at > from ? at - 1 : at, 0, items.Count - 1);
            if (from < 0 || from == to)
                return;
            items.Move(from, to);
            Save();
            RefreshIndexes();
            AppsBox.SelectedIndex = to;
        }
    }

    // Dragging apps onto another list in the sidebar: move, or copy with Ctrl held.

    /// <summary>The list row under the pointer that a dragged app may go to: any list but the one it comes from.</summary>
    private ListRow? DropTarget(DragEventArgs e) =>
        e.Data.GetDataPresent(AppDragFormat)
        && e.OriginalSource is DependencyObject source
        && ItemsControl.ContainerFromElement(ListsBox, source) is ListBoxItem { DataContext: ListRow row }
        && !string.Equals(row.Name, currentList, StringComparison.OrdinalIgnoreCase)
            ? row
            : null;

    private static bool IsCopy(DragEventArgs e) => e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);

    private void HighlightDropTarget(ListRow? target)
    {
        foreach (var row in ListsBox.Items.OfType<ListRow>())
            row.IsDropTarget = row == target;
    }

    private void ListsBox_DragOver(object sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        HighlightDropTarget(target);
        e.Effects = target is null ? DragDropEffects.None : IsCopy(e) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void ListsBox_DragLeave(object sender, DragEventArgs e) => HighlightDropTarget(null);

    /// <summary>
    /// Appends a copy of the dragged item to the target list and, for a move, removes it from this one, in one
    /// update; refused like any save when this list changed elsewhere since it was loaded.
    /// </summary>
    private void ListsBox_Drop(object sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        HighlightDropTarget(null);
        if (target is null || e.Data.GetData(AppDragFormat) is not AppItem dragged)
            return;
        var index = items.IndexOf(dragged);
        if (index < 0)
            return;
        var copy = IsCopy(e);
        var from = currentList;
        var baseline = listBaseline;
        var json = System.Text.Json.JsonSerializer.Serialize(dragged.Entry, ConfigStore.JsonOptions);
        if (!TryUpdate(c =>
            {
                if (!c.Lists.TryGetValue(from, out var source) || Fingerprint(source) != baseline || !c.Lists.TryGetValue(target.Name, out var destination))
                    throw new ListChangedElsewhere();
                destination.Add(System.Text.Json.JsonSerializer.Deserialize<Entry>(json, ConfigStore.JsonOptions)!);
                if (!copy)
                    source.RemoveAt(index);
            }))
            return;
        if (!copy)
        {
            items.RemoveAt(index);
            listBaseline = Fingerprint(items.Select(i => i.Entry).ToList());
            RefreshIndexes();
            UpdatePages();
        }
        StatusText.Text = $"{(copy ? "Copied" : "Moved")} \u201c{dragged.Name}\u201d to \u201c{target.Name}\u201d.";
    }

    private int DropIndex(DragEventArgs e)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (AppsBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;
            var pos = e.GetPosition(container);
            if (pos.Y < container.ActualHeight / 2)
                return i;
            if (pos.Y < container.ActualHeight)
                return i + 1;
        }
        return items.Count;
    }

    // Details

    private void AppsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StatusText.Text = "";
        ShowDetails();
    }

    private void ShowDetails()
    {
        filling = true;
        var item = Selected;
        Details.IsEnabled = item is { IsBreak: false };
        RemoveButton.IsEnabled = item is not null;
        var entry = item?.Entry ?? new Entry();
        TitleBox.Text = entry.Title ?? "";
        TargetBox.Text = entry.Target;
        ArgsBox.Text = entry.Args ?? "";
        WorkDirBox.Text = entry.WorkDir ?? "";
        TrackPathBox.Text = entry.TrackPath ?? "";
        IconBox.Text = entry.Icon ?? "";
        Select(KindBox, entry.Kind?.ToString().ToLowerInvariant());
        Select(OnRunningBox, entry.OnRunning?.ToString().ToLowerInvariant());
        Select(LongPressBox, entry.LongPress?.ToString().ToLowerInvariant());
        Select(KillModeBox, entry.KillMode?.ToString().ToLowerInvariant());
        Select(AdminBox, entry.Admin?.ToString().ToLowerInvariant());
        Select(IndicatorBox, entry.Indicator?.ToString().ToLowerInvariant());
        PreviewImage.Source = item?.Icon;
        shownState = null;
        ResolvedText.Text = item is null ? "" : Describe(TargetResolver.Resolve(entry));
        filling = false;
    }

    /// <summary>The "default" choice of each behaviour picker names the built-in value the app gets when it sets none.</summary>
    private void ShowInherited()
    {
        var d = LaunchOptions.Defaults;
        SetInherited(OnRunningBox, d.OnRunning.ToString());
        SetInherited(LongPressBox, d.LongPress.ToString());
        SetInherited(KillModeBox, d.KillMode.ToString());
        SetInherited(AdminBox, d.Admin ? "true" : "false");
        SetInherited(IndicatorBox, d.Indicator.ToString());
    }

    private static void SetInherited(ComboBox box, string value)
    {
        var choices = box.Items.OfType<ComboBoxItem>().ToList();
        var text = choices.Skip(1).FirstOrDefault(i => string.Equals(i.Tag as string, value, StringComparison.OrdinalIgnoreCase))?.Content as string ?? value;
        choices[0].Content = $"default: {text}";
    }

    private static string Describe(ResolvedTarget t)
    {
        if (t.Error is not null)
            return "⚠ " + t.Error;
        var lines = new List<string> { $"launches {t.LaunchFile ?? t.Aumid}" };
        if ((t.MatchPath ?? t.Aumid) is { } match && match != (t.LaunchFile ?? t.Aumid))
            lines.Add($"running dot follows {match}");
        return string.Join("\n", lines);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (filling || Selected is not { } item || sender is not TextBox box)
            return;
        var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        var entry = item.Entry;
        var old = box.Tag switch
        {
            "title" => entry.Title,
            "target" => entry.Target,
            "args" => entry.Args,
            "workDir" => entry.WorkDir,
            "trackPath" => entry.TrackPath,
            "icon" => entry.Icon,
            _ => value,
        };
        if (value == old)
            return;
        switch (box.Tag)
        {
            case "title": entry.Title = value; break;
            case "target":
                if (value is null) return;
                entry.Target = value;
                break;
            case "args": entry.Args = value; break;
            case "workDir": entry.WorkDir = value; break;
            case "trackPath": entry.TrackPath = value; break;
            case "icon": entry.Icon = value; break;
        }
        Commit(item);
    }

    private void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (filling || Selected is not { } item || sender is not ComboBox box)
            return;
        var value = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = item.Entry;
        if (box == KindBox) entry.Kind = Parse<TargetKind>(value);
        else if (box == OnRunningBox) entry.OnRunning = Parse<OnRunning>(value);
        else if (box == LongPressBox) entry.LongPress = Parse<LongPressMode>(value);
        else if (box == KillModeBox) entry.KillMode = Parse<KillMode>(value);
        else if (box == IndicatorBox) entry.Indicator = Parse<IndicatorStyle>(value);
        else if (box == AdminBox) entry.Admin = value is null or "" ? null : value == "true";
        Commit(item);
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
            return;
        var dialog = new OpenFileDialog { Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*", DereferenceLinks = false };
        if (CurrentTargetFile(item.Entry) is { } current)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }
        if (dialog.ShowDialog(this) != true)
            return;
        item.Entry.Target = dialog.FileName;
        item.Entry.Kind = null;
        Commit(item);
        ShowDetails();
    }

    // Test key: the preview icon behaves like the app's key on the deck, through the same AppRuntime rules.

    private readonly AppRuntime tester = new(new PressExecutor(new Win32AppOperations()));
    private readonly Win32ProcessSource processes = new();
    private DispatcherTimer? testPoll;
    private DispatcherTimer? holdTimer;
    private RunState? shownState;
    private (AppItem Item, DateTime At)? pendingLaunch;

    /// <summary>Polls the selected app's state at the plugin's rate so the preview's dot and the startup timing stay live.</summary>
    private void StartTestPoll()
    {
        testPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(250, config.PollMs)) };
        testPoll.Tick += (_, _) => PollTestState();
        testPoll.Start();
    }

    private void PollTestState()
    {
        if (Selected is not { IsBreak: false } item)
            return;
        var state = tester.StateOf(item.Entry, new ProcessSnapshot(processes.Snapshot()));
        if (pendingLaunch is { } launch && launch.Item == item && state is not RunState.Starting)
        {
            var seconds = (DateTime.UtcNow - launch.At).TotalSeconds;
            StatusText.Text = state is RunState.Stopped
                ? $"{item.Name} stopped without showing a window."
                : $"{item.Name} showed its window after {seconds:0.0} s.";
            pendingLaunch = null;
        }
        if (state == shownState)
            return;
        shownState = state;
        if (state == RunState.Stopped)
        {
            PreviewImage.Source = item.Icon;
            return;
        }
        var entry = item.Entry;
        var snapshot = config;
        App.Shell.RunAsync(() => KeyImages.Render(entry, snapshot, state)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && Selected == item && shownState == state)
                PreviewImage.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void TestKey_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not { IsBreak: false } item)
            return;
        TestKey.CaptureMouse();
        TestKey.Opacity = 0.6;
        holdTimer?.Stop();
        holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(config.LongPressMs) };
        holdTimer.Tick += async (_, _) =>
        {
            holdTimer.Stop();
            TestKey.Opacity = 1;
            await TestPressAsync(item, isLong: true);
        };
        holdTimer.Start();
    }

    private async void TestKey_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        TestKey.ReleaseMouseCapture();
        TestKey.Opacity = 1;
        if (holdTimer is not { IsEnabled: true } timer || Selected is not { IsBreak: false } item)
            return;
        timer.Stop();
        await TestPressAsync(item, isLong: false);
    }

    private async Task TestPressAsync(AppItem item, bool isLong)
    {
        var press = isLong ? "Long press" : "Short press";
        var outcome = await tester.PressAsync(item.Entry, isLong, new ProcessSnapshot(processes.Snapshot()), config);
        StatusText.Text = outcome.Result.Success
            ? $"{press} \u2192 {Describe(outcome.Decision)}."
            : $"{press} \u2192 {Describe(outcome.Decision)} failed: {outcome.Result.Error}";
        pendingLaunch = outcome.Launched ? (item, DateTime.UtcNow) : null;
        PollTestState();
    }

    private static string Describe(PressDecision decision) => decision switch
    {
        PressDecision.Launch => "launched",
        PressDecision.LaunchAdmin => "launched as administrator",
        PressDecision.Relaunch => "launched another instance",
        PressDecision.FocusOrRelaunch => "brought to front, or launched again when it had no window",
        PressDecision.Focus => "brought to front",
        PressDecision.FocusIfAny => "still starting, brought to front if it has a window",
        PressDecision.Close => "closed",
        PressDecision.Nothing => "nothing (set to do nothing while running)",
        _ => "refused",
    };

    private void PickIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
            return;
        var resolved = TargetResolver.Resolve(item.Entry);
        var current = item.Entry.Icon is { } icon ? TargetResolver.Expand(icon) : null;
        var picker = new IconPickerWindow(current, resolved.ShortcutExe ?? resolved.LaunchFile) { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen == item.Entry.Icon)
            return;
        item.Entry.Icon = picker.Chosen;
        Commit(item);
        ShowDetails();
    }

    /// <summary>The file the target points at (the shortcut itself for a .lnk, the newest match for a versioned path), if it exists.</summary>
    private static string? CurrentTargetFile(Entry entry)
    {
        var resolved = TargetResolver.Resolve(entry);
        var file = resolved.Kind == TargetKind.Lnk ? TargetResolver.Expand(entry.Target) : resolved.LaunchFile;
        return file is not null && File.Exists(file) ? file : null;
    }

    private void Commit(AppItem item)
    {
        item.Refresh();
        Save();
        LoadIcon(item);
        ResolvedText.Text = Describe(TargetResolver.Resolve(item.Entry));
        item.PropertyChanged += UpdatePreviewOnce;
    }

    private void UpdatePreviewOnce(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not AppItem item || e.PropertyName != nameof(AppItem.Icon))
            return;
        item.PropertyChanged -= UpdatePreviewOnce;
        if (item == Selected)
            PreviewImage.Source = item.Icon;
    }

    private static T? Parse<T>(string? value) where T : struct, Enum =>
        string.IsNullOrEmpty(value) ? null : Enum.Parse<T>(value, ignoreCase: true);

    private static void FillCombo(ComboBox box, params (string Value, string Text)[] options)
    {
        foreach (var (value, text) in options)
            box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
    }

    private static void Select(ComboBox box, string? value)
    {
        value ??= "";
        box.SelectedItem = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
}
