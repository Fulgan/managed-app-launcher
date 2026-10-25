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
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(1);

    private readonly string path = AppPaths.ConfigFile;
    private readonly DispatcherTimer reloadTimer;
    private readonly FileSystemWatcher watcher;
    private AppConfig config = new AppConfig().Normalize();
    private ObservableCollection<AppItem> items = [];
    private string currentList = AppConfig.DefaultListName;
    private DateTime lastOwnWrite = DateTime.MinValue;
    private bool filling;
    private Point dragStart;
    private bool dragArmed;

    public MainWindow()
    {
        InitializeComponent();
        FillCombo(KindBox, ("", "detect from target"), ("lnk", "shortcut"), ("exe", "exe"), ("glob", "versioned path"), ("aumid", "app id (AUMID)"));
        FillCombo(OnRunningBox, ("", "from preset"), ("auto", "focus, else relaunch"), ("focus", "focus"), ("relaunch", "relaunch"), ("none", "do nothing"));
        FillCombo(LongPressBox, ("", "from preset"), ("auto", "kill / run as admin"), ("none", "same as short press"));
        FillCombo(KillModeBox, ("", "from preset"), ("all", "all instances"), ("newest", "newest instance"));
        FillCombo(AdminBox, ("", "from preset"), ("true", "always"), ("false", "never"));
        FillCombo(IndicatorBox, ("", "from preset"), ("dot", "dot"), ("border", "border"));

        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        reloadTimer.Tick += (_, _) =>
        {
            reloadTimer.Stop();
            if (DateTime.UtcNow - lastOwnWrite > OwnWriteWindow)
                Reload(keepSelection: true);
        };

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

    private void Reload(bool keepSelection)
    {
        if (!ConfigStore.TryLoad(path, out var loaded, out var error))
        {
            StatusText.Text = $"config.json is invalid, not reloaded: {error}";
            return;
        }
        config = loaded;
        var selected = keepSelection ? AppsBox.SelectedIndex : -1;
        if (!config.Lists.ContainsKey(currentList))
            currentList = config.HomeList;

        filling = true;
        ListsBox.ItemsSource = ListNames().Select(name => new ListRow(name)).ToList();
        ListsBox.SelectedItem = ListsBox.Items.Cast<ListRow>().First(r => string.Equals(r.Name, currentList, StringComparison.OrdinalIgnoreCase));
        PresetBox.Items.Clear();
        foreach (var preset in config.Presets.Keys.Order())
            PresetBox.Items.Add(new ComboBoxItem { Content = preset, Tag = preset });
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
        try
        {
            lastOwnWrite = DateTime.UtcNow;
            config = ConfigStore.Update(path, c =>
            {
                if (on)
                    c.ListOptions[list] = new ListOptions { ReturnAfterLaunch = true };
                else
                    c.ListOptions.Remove(list);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"could not save: {ex.Message}";
        }
    }

    private void Deck_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (filling || sender is not ComboBox { Tag: string id, SelectedItem: ComboBoxItem { Tag: string list } })
            return;
        try
        {
            lastOwnWrite = DateTime.UtcNow;
            config = ConfigStore.Update(path, c =>
            {
                if (list.Length == 0)
                    c.Decks.Remove(id);
                else
                    c.Decks[id] = list;
            });
            StatusText.Text = list.Length == 0 ? "Deck freed: it shows whatever profile you pick in Stream Deck." : $"Deck now always shows '{list}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"could not save: {ex.Message}";
        }
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
        items = new ObservableCollection<AppItem>(config.GetList(currentList).Select(e => new AppItem(e)));
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
        var entry = item.Entry;
        var snapshot = config;
        App.Shell.RunAsync(() => KeyImages.Render(entry, snapshot)).ContinueWith(
            t => { if (t.Status == TaskStatus.RanToCompletion) item.Icon = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdatePages()
    {
        int Pages(int keys) => new LauncherLayout(keys, items.Count).PageCount;
        PagesText.Text = $"{items.Count} apps · pages on Stream Deck: {Pages(15)} · XL: {Pages(32)} · Mini: {Pages(6)}";
    }

    /// <summary>Writes only the current list into the latest file content, so concurrent CLI edits to other lists survive.</summary>
    private void Save()
    {
        var entries = items.Select(i => i.Entry).ToList();
        var list = currentList;
        try
        {
            lastOwnWrite = DateTime.UtcNow;
            config = ConfigStore.Update(path, c => c.Lists[list] = entries);
            UpdatePages();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"could not save: {ex.Message}";
        }
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
        currentList = selected.Name;
        ShowList();
    }

    private void AddList_Click(object sender, RoutedEventArgs e)
    {
        string? name = null;
        lastOwnWrite = DateTime.UtcNow;
        ConfigStore.Update(path, c => c.AddList(name = c.UnusedListName("New list")));
        currentList = name!;
        Reload(keepSelection: false);
        BeginRename();
    }

    private void DuplicateList_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Name is not { } source)
            return;
        string? name = null;
        lastOwnWrite = DateTime.UtcNow;
        ConfigStore.Update(path, c => name = c.CopyList(source));
        currentList = name!;
        Reload(keepSelection: false);
        StatusText.Text = $"Copied '{source}' to '{name}'.";
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
        string? error = config.CheckListName(from, name);
        if (error is null)
        {
            try
            {
                lastOwnWrite = DateTime.UtcNow;
                config = ConfigStore.Update(path, c => error = c.RenameList(from, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                error = $"could not save: {ex.Message}";
            }
        }
        if (error is not null)
        {
            StatusText.Text = $"Name not changed. {error}";
            return;
        }
        if (string.Equals(currentList, from, StringComparison.OrdinalIgnoreCase))
            currentList = name;
        Dispatcher.BeginInvoke(() => Reload(keepSelection: true), DispatcherPriority.Background);
        StatusText.Text = $"Renamed '{from}' to '{name}'. Stream Deck keys that used the old name follow it.";
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
                $"Its {count} app{(count == 1 ? "" : "s")} will be removed too. Keys showing this list fall back to the home list.", "Remove"))
            return;
        lastOwnWrite = DateTime.UtcNow;
        ConfigStore.Update(path, c => c.Lists.Remove(name));
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
            AddPaths(picker.Chosen, InsertIndex());
    }

    private void AddRunning_Click(object sender, RoutedEventArgs e)
    {
        var picker = AppPickerWindow.Running();
        picker.Owner = this;
        if (picker.ShowDialog() == true)
            AddPaths(picker.Chosen, InsertIndex());
    }

    private int InsertIndex() => AppsBox.SelectedIndex >= 0 ? AppsBox.SelectedIndex + 1 : items.Count;

    private void AddPaths(IEnumerable<string> paths, int at)
    {
        var advice = new List<string>();
        var index = Math.Clamp(at, 0, items.Count);
        var first = index;
        foreach (var p in paths)
        {
            var plan = PinAdvisor.Plan(p, null, null);
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
        StatusText.Text = advice.Count > 0 ? string.Join("   ", advice) : $"Added {index - first} app(s).";
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
        var index = AppsBox.SelectedIndex;
        if (index < 0)
            return;
        if (!await ConfirmAsync($"Remove “{items[index].Name}”?",
                $"It leaves the list “{currentList}” and its key on the deck.", "Remove"))
            return;
        if (index >= items.Count)
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
        DragDrop.DoDragDrop(AppsBox, new DataObject(AppDragFormat, item), DragDropEffects.Move);
    }

    private void AppsBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(AppDragFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AppsBox_Drop(object sender, DragEventArgs e)
    {
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

    private void AppsBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    private void ShowDetails()
    {
        filling = true;
        var item = Selected;
        Details.IsEnabled = item is not null;
        RemoveButton.IsEnabled = item is not null;
        var entry = item?.Entry ?? new Entry();
        TitleBox.Text = entry.Title ?? "";
        TargetBox.Text = entry.Target;
        ArgsBox.Text = entry.Args ?? "";
        WorkDirBox.Text = entry.WorkDir ?? "";
        TrackPathBox.Text = entry.TrackPath ?? "";
        IconBox.Text = entry.Icon ?? "";
        Select(KindBox, entry.Kind?.ToString().ToLowerInvariant());
        Select(PresetBox, entry.Preset);
        Select(OnRunningBox, entry.OnRunning?.ToString().ToLowerInvariant());
        Select(LongPressBox, entry.LongPress?.ToString().ToLowerInvariant());
        Select(KillModeBox, entry.KillMode?.ToString().ToLowerInvariant());
        Select(AdminBox, entry.Admin?.ToString().ToLowerInvariant());
        Select(IndicatorBox, entry.Indicator?.ToString().ToLowerInvariant());
        PreviewImage.Source = item?.Icon;
        ResolvedText.Text = item is null ? "" : Describe(TargetResolver.Resolve(entry));
        filling = false;
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
        else if (box == PresetBox) entry.Preset = value ?? AppConfig.DefaultPresetName;
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
        if (dialog.ShowDialog(this) != true)
            return;
        item.Entry.Target = dialog.FileName;
        item.Entry.Kind = null;
        Commit(item);
        ShowDetails();
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
