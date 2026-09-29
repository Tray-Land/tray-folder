using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using TrayFolder.Core;
using TrayFolder.Models;
using TrayFolder.Services;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Windows.UI.Core;

namespace TrayFolder.Views;

/// <summary>
/// The folder view: a list of the chosen folder's files that drag straight out into other apps.
/// It reads the disk each time the flyout opens and watches for changes only while visible.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private const double ListThumbnailSize = 32;
    private const double TileThumbnailSize = 96;

    private readonly TrayFlyoutWindow _host;
    private readonly FolderWatcher _watcher;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly Dictionary<string, FileItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly FolderViewOptions _options = SettingsService.ViewOptions;

    private string _rootPath = FolderService.RootPath;
    private string _currentPath;
    private List<FileEntry> _entries = [];
    private List<FileItem> _visible = [];
    private List<(string Header, int Count)> _lastGroupHeaders = [];
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _isVisible;
    private FileItem? _menuTarget;

    public FlyoutPage(TrayFlyoutWindow host)
    {
        InitializeComponent();
        _host = host;
        _currentPath = _rootPath;

        _watcher = new FolderWatcher(DispatcherQueue);
        _watcher.Changed += (_, _) => _ = LoadAsync();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(150);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => Arrange();

        // Rows handle their own pointer input for selection and drag; listen to what they've handled.
        foreach (ListViewBase view in new ListViewBase[] { FileList, FileGrid })
        {
            view.AddHandler(TappedEvent, new TappedEventHandler(Items_Tapped), handledEventsToo: true);
            view.AddHandler(PointerPressedEvent, new PointerEventHandler(Items_PointerPressed), handledEventsToo: true);
        }

        FolderService.RootChanged += FolderService_RootChanged;
        ApplyViewChrome();
        UpdateHeader();
    }

    private ListViewBase ActiveView => _options.View == ViewMode.Tiles ? FileGrid : FileList;

    /// <summary>The flyout opened: re-read the folder and watch it until the flyout hides.</summary>
    public void OnShown()
    {
        _isVisible = true;
        _watcher.Watch(_currentPath);
        _ = LoadAsync();
        ActiveView.Focus(FocusState.Programmatic);
    }

    /// <summary>The flyout hid: stop watching and drop any read in flight.</summary>
    public void OnHidden()
    {
        _isVisible = false;
        _watcher.Stop();
        _loadCancellation?.Cancel();
    }

    public void Dispose()
    {
        OnHidden();
        _watcher.Dispose();
        _searchTimer.Stop();
        FolderService.RootChanged -= FolderService_RootChanged;
    }

    /// <summary>Escape inside the page: clear the search first. Returns true when it did something.</summary>
    public bool HandleEscape()
    {
        if (SearchBox.Text.Length == 0)
        {
            return false;
        }

        SearchBox.Text = string.Empty;
        Arrange();
        ActiveView.Focus(FocusState.Keyboard);
        return true;
    }

    private void FolderService_RootChanged(object? sender, EventArgs e)
    {
        _rootPath = FolderService.RootPath;
        NavigateTo(_rootPath);
    }

    private void NavigateTo(string path)
    {
        _currentPath = path;
        _entries = [];
        _items.Clear();
        SearchBox.Text = string.Empty;
        UpdateHeader();
        Arrange(force: true);

        if (_isVisible)
        {
            _watcher.Watch(_currentPath);
            _ = LoadAsync();
        }
    }

    private void GoUp()
    {
        if (!IsSamePath(_currentPath, _rootPath) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_currentPath)) is { } parent)
        {
            NavigateTo(parent);
        }
    }

    private async Task LoadAsync()
    {
        _loadCancellation?.Cancel();
        CancellationTokenSource cancellation = _loadCancellation = new CancellationTokenSource();
        int generation = ++_loadGeneration;
        LoadingRing.IsActive = true;

        try
        {
            if (!Directory.Exists(_currentPath))
            {
                // A subfolder that went away falls back to the root; a missing root needs the user.
                if (!IsSamePath(_currentPath, _rootPath) && Directory.Exists(_rootPath))
                {
                    NavigateTo(_rootPath);
                    return;
                }

                ShowStatus("Can't find this folder", $"{_currentPath} was moved or deleted.", canChoose: true);
                _entries = [];
                Arrange(force: true);
                return;
            }

            List<FileEntry> entries = await FolderService.EnumerateAsync(_currentPath, cancellation.Token);
            if (generation != _loadGeneration)
            {
                return;
            }

            StatusBar.IsOpen = false;
            _entries = entries;
            Arrange();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (generation == _loadGeneration)
        {
            ShowStatus("Can't read this folder", ex.Message, canChoose: IsSamePath(_currentPath, _rootPath));
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    /// <summary>Applies search, filters, sort and grouping to the last read of the folder.</summary>
    private void Arrange(bool force = false)
    {
        Dictionary<string, FileItem> items = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry entry in _entries)
        {
            // Keep rows whose file hasn't changed, so their thumbnails and StorageItems survive a refresh.
            if (!_items.TryGetValue(entry.FullPath, out FileItem? item) || item.Entry != entry)
            {
                item = new FileItem(entry);
            }

            item.ShowExtension = _options.ShowExtensions;
            items[entry.FullPath] = item;
        }

        _items.Clear();
        foreach ((string path, FileItem item) in items)
        {
            _items[path] = item;
        }

        IReadOnlyList<EntryGroup> groups = FolderViewPolicy.Arrange(
            _entries, _options, SearchBox.Text, DateTime.Now, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        List<FileGroup> fileGroups = groups.Select(g => new FileGroup(g.Header, g.Items.Select(e => _items[e.FullPath]))).ToList();
        List<FileItem> visible = fileGroups.SelectMany(g => g.Items).ToList();

        // Opening the flyout re-reads the folder; if nothing moved, leave the list (and its scroll) alone.
        bool unchanged = !force && ActiveView.ItemsSource is not null && visible.SequenceEqual(_visible)
            && GroupHeadersMatch(fileGroups);
        _visible = visible;
        if (!unchanged)
        {
            HashSet<string> selected = ActiveView.SelectedItems.OfType<FileItem>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ListViewBase inactive = ActiveView == FileList ? FileGrid : FileList;
            inactive.ItemsSource = null;
            ActiveView.ItemsSource = _options.GroupBy == GroupField.None
                ? visible
                : new CollectionViewSource { Source = fileGroups, IsSourceGrouped = true, ItemsPath = new PropertyPath(nameof(FileGroup.Items)) }.View;
            _lastGroupHeaders = fileGroups.Select(g => (g.Header, g.Items.Count)).ToList();

            foreach (FileItem item in visible.Where(i => selected.Contains(i.FullPath)))
            {
                ActiveView.SelectedItems.Add(item);
            }
        }

        UpdateEmptyState();
        UpdateCount();
    }

    private bool GroupHeadersMatch(List<FileGroup> groups) =>
        groups.Select(g => (g.Header, g.Items.Count)).SequenceEqual(_lastGroupHeaders);

    private void UpdateEmptyState()
    {
        bool empty = _visible.Count == 0 && !StatusBar.IsOpen;
        bool filtered = SearchBox.Text.Length > 0 || _options.IsFiltered || (!_options.ShowHidden && _entries.Any(e => e.IsHidden));
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _entries.Count == 0 ? "This folder is empty." : "Nothing here matches.";
        ClearFiltersButton.Visibility = empty && _entries.Count > 0 && filtered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        int total = _entries.Count(e => _options.ShowHidden || !e.IsHidden);
        string count = _visible.Count == total
            ? (total == 1 ? "1 item" : $"{total} items")
            : $"{_visible.Count} of {total} items";
        int selected = ActiveView.SelectedItems.Count;
        CountText.Text = selected > 1 ? $"{count} · {selected} selected · drag to copy" : count;
    }

    private void UpdateHeader()
    {
        FolderTitle.Text = FolderService.DisplayName(_rootPath);
        App.Current.SetTrayStatus(FolderTitle.Text);
        ToolTipService.SetToolTip(FolderButton, _rootPath);
        FolderGlyph.Glyph = FolderService.KnownFolders().FirstOrDefault(k => IsSamePath(k.Path, _rootPath)).Glyph ?? "";

        // Breadcrumb only once the user has gone into a subfolder.
        if (IsSamePath(_currentPath, _rootPath))
        {
            Breadcrumb.Visibility = Visibility.Collapsed;
            Breadcrumb.ItemsSource = null;
            return;
        }

        string relative = Path.GetRelativePath(_rootPath, _currentPath);
        List<string> crumbs = [FolderService.DisplayName(_rootPath), .. relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)];
        Breadcrumb.ItemsSource = crumbs;
        Breadcrumb.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string title, string message, bool canChoose)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.ActionButton.Visibility = canChoose ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.IsOpen = true;
        UpdateEmptyState();
    }

    private static bool IsSamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // ---- Opening, previewing and dragging rows ----

    private void Activate(FileItem item)
    {
        if (item.IsFolder)
        {
            NavigateTo(item.FullPath);
            return;
        }

        _host.ShowDetailPage(item, _visible.Where(i => !i.IsFolder).ToList());
    }

    private static FileItem? ItemFromSource(object source) => (source as FrameworkElement)?.DataContext as FileItem;

    private static bool IsModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Items_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Ctrl/Shift+click builds a selection to drag; a plain click previews.
        if (ItemFromSource(e.OriginalSource) is { } item && !IsModifierDown(VirtualKey.Control) && !IsModifierDown(VirtualKey.Shift))
        {
            Activate(item);
        }
    }

    private void Items_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A press may be the start of a drag, and drag events can't wait: resolve the file now.
        if (ItemFromSource(e.OriginalSource) is { } item)
        {
            _ = item.GetStorageItemAsync();
        }
    }

    private void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        List<FileItem> selected = ActiveView.SelectedItems.OfType<FileItem>().ToList();
        switch (e.Key)
        {
            case VirtualKey.Enter when selected.Count == 1:
                Activate(selected[0]);
                e.Handled = true;
                break;
            case VirtualKey.C when IsModifierDown(VirtualKey.Control) && selected.Count > 0:
                _ = CopyAsync(selected);
                e.Handled = true;
                break;
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Backspace goes up a folder, as in Explorer, unless it's editing the search text.
        if (e.Key == VirtualKey.Back && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            GoUp();
            e.Handled = true;
        }
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (FileItem item in e.AddedItems.OfType<FileItem>())
        {
            _ = item.GetStorageItemAsync();
        }

        UpdateCount();
    }

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        List<FileItem> items = e.Items.OfType<FileItem>().ToList();
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        FileActions.FillDragData(e.Data, items);
        _host.SetDragging(true);
    }

    private void Items_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _host.SetDragging(false);

    private void Items_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItem)
        {
            return;
        }

        // Thumbnails in a second phase, so fast scrolling lays out names first.
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, Items_ContainerContentChanging);
            return;
        }

        double logical = _options.View == ViewMode.Tiles ? TileThumbnailSize : ListThumbnailSize;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        FileItem item = (FileItem)args.Item;
        uint size = (uint)Math.Ceiling(logical * scale);

        // Start outside the layout pass that raised this event.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = item.LoadThumbnailAsync(size));
    }

    private async Task CopyAsync(IReadOnlyList<FileItem> items)
    {
        if (await FileActions.CopyToClipboardAsync(items))
        {
            CountText.Text = items.Count == 1 ? "Copied 1 item" : $"Copied {items.Count} items";
        }
    }

    // ---- Row context menu ----

    private IReadOnlyList<FileItem> MenuItems()
    {
        if (_menuTarget is null)
        {
            return [];
        }

        // Right-clicking inside a selection acts on the whole selection, like Explorer.
        List<FileItem> selected = ActiveView.SelectedItems.OfType<FileItem>().ToList();
        return selected.Contains(_menuTarget) ? selected : [_menuTarget];
    }

    private void ItemMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }

        // Items are fixed in XAML: [0] Preview, [2] Open with.
        _menuTarget = menu.Target?.DataContext as FileItem;
        bool single = _menuTarget is not null && MenuItems().Count == 1;
        MenuFlyoutItem preview = (MenuFlyoutItem)menu.Items[0];
        MenuFlyoutItem openWith = (MenuFlyoutItem)menu.Items[2];
        preview.Text = _menuTarget?.IsFolder == true ? "Open folder here" : "Preview";
        preview.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        openWith.Visibility = single && _menuTarget?.IsFolder == false ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not null)
        {
            Activate(_menuTarget);
        }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        foreach (FileItem item in MenuItems())
        {
            FileActions.Open(item.FullPath);
        }
    }

    private void MenuOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not null)
        {
            FileActions.OpenWith(_menuTarget.FullPath);
        }
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => _ = CopyAsync(MenuItems());

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e) => FileActions.CopyPaths(MenuItems());

    private void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not null)
        {
            FileActions.ShowInExplorer(_menuTarget.FullPath);
        }
    }

    // ---- Header ----

    private void FolderMenu_Opening(object sender, object e)
    {
        FolderMenu.Items.Clear();
        HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string path, string glyph) in FolderService.KnownFolders())
        {
            FolderMenu.Items.Add(FolderMenuItem(name, path, glyph));
            shown.Add(Path.TrimEndingDirectorySeparator(path));
        }

        List<string> recent = SettingsService.RecentFolders
            .Where(p => !shown.Contains(Path.TrimEndingDirectorySeparator(p)) && Directory.Exists(p))
            .ToList();
        if (!shown.Contains(Path.TrimEndingDirectorySeparator(_rootPath)) && !recent.Contains(_rootPath, StringComparer.OrdinalIgnoreCase))
        {
            recent.Insert(0, _rootPath);
        }

        if (recent.Count > 0)
        {
            FolderMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (string path in recent)
            {
                MenuFlyoutItem item = FolderMenuItem(FolderService.DisplayName(path), path, "");
                ToolTipService.SetToolTip(item, path);
                FolderMenu.Items.Add(item);
            }
        }

        FolderMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem choose = new() { Text = "Choose folder…", Icon = new FontIcon { Glyph = "" } };
        choose.Click += ChooseFolder_Click;
        FolderMenu.Items.Add(choose);
    }

    private MenuFlyoutItem FolderMenuItem(string name, string path, string glyph)
    {
        RadioMenuFlyoutItem item = new()
        {
            Text = name,
            GroupName = "Folder",
            IsChecked = IsSamePath(path, _rootPath),
            Icon = new FontIcon { Glyph = glyph },
        };
        item.Click += (_, _) =>
        {
            if (!IsSamePath(path, _rootPath))
            {
                FolderService.SetRoot(path);
            }
            else if (!IsSamePath(_currentPath, _rootPath))
            {
                NavigateTo(_rootPath);
            }
        };
        return item;
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await _host.PickFolderAsync(_rootPath) is { } path)
        {
            FolderService.SetRoot(path);
        }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e) => FileActions.OpenFolder(_currentPath);

    private void PinButton_Changed(object sender, RoutedEventArgs e) => _host.IsPinned = PinButton.IsChecked == true;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        string path = _rootPath;
        if (Breadcrumb.ItemsSource is List<string> crumbs)
        {
            foreach (string segment in crumbs.Skip(1).Take(args.Index))
            {
                path = Path.Combine(path, segment);
            }
        }

        NavigateTo(path);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down || e.Key == VirtualKey.Enter)
        {
            _searchTimer.Stop();
            Arrange();
            if (_visible.Count > 0)
            {
                ActiveView.SelectedIndex = -1;
                ActiveView.SelectedItem = _visible[0];
                ActiveView.Focus(FocusState.Keyboard);
            }

            e.Handled = true;
        }
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    private void GoUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        GoUp();
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = LoadAsync();
        args.Handled = true;
    }

    // ---- Sort, filter and view menus (built on open so they always show the current state) ----

    private void SortMenu_Opening(object sender, object e)
    {
        SortMenu.Items.Clear();
        AddRadio(SortMenu.Items, "Name", "Sort", _options.SortBy == SortField.Name, () => SortBy(SortField.Name));
        AddRadio(SortMenu.Items, "Date modified", "Sort", _options.SortBy == SortField.DateModified, () => SortBy(SortField.DateModified));
        AddRadio(SortMenu.Items, "Type", "Sort", _options.SortBy == SortField.Type, () => SortBy(SortField.Type));
        AddRadio(SortMenu.Items, "Size", "Sort", _options.SortBy == SortField.Size, () => SortBy(SortField.Size));
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        AddRadio(SortMenu.Items, "Ascending", "Direction", !_options.Descending, () => _options.Descending = false, "");
        AddRadio(SortMenu.Items, "Descending", "Direction", _options.Descending, () => _options.Descending = true, "");
        SortMenu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutSubItem group = new() { Text = "Group by", Icon = new FontIcon { Glyph = "" } };
        AddRadio(group.Items, "Date modified", "Group", _options.GroupBy == GroupField.DateModified, () => _options.GroupBy = GroupField.DateModified);
        AddRadio(group.Items, "Name", "Group", _options.GroupBy == GroupField.Name, () => _options.GroupBy = GroupField.Name);
        AddRadio(group.Items, "Kind", "Group", _options.GroupBy == GroupField.Kind, () => _options.GroupBy = GroupField.Kind);
        AddRadio(group.Items, "Size", "Group", _options.GroupBy == GroupField.Size, () => _options.GroupBy = GroupField.Size);
        group.Items.Add(new MenuFlyoutSeparator());
        AddRadio(group.Items, "(None)", "Group", _options.GroupBy == GroupField.None, () => _options.GroupBy = GroupField.None);
        SortMenu.Items.Add(group);
    }

    private void SortBy(SortField field)
    {
        if (_options.SortBy != field)
        {
            // Explorer's first click on a column: A to Z for text, newest and largest first for numbers.
            _options.Descending = field is SortField.DateModified or SortField.Size;
        }

        _options.SortBy = field;
    }

    private void FilterMenu_Opening(object sender, object e)
    {
        FilterMenu.Items.Clear();
        foreach (FileKind kind in new[] { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Document, FileKind.Archive, FileKind.Program, FileKind.Other })
        {
            AddToggle(FilterMenu.Items, FileKinds.PluralLabel(kind), _options.Kinds.Contains(kind), on =>
            {
                _options.Kinds.Remove(kind);
                if (on)
                {
                    _options.Kinds.Add(kind);
                }
            }, FileKinds.Glyph(kind));
        }

        FilterMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutSubItem date = new() { Text = "Date modified", Icon = new FontIcon { Glyph = "" } };
        AddRadio(date.Items, "Any time", "Date", _options.Date == DateFilter.Any, () => _options.Date = DateFilter.Any);
        AddRadio(date.Items, "Today", "Date", _options.Date == DateFilter.Today, () => _options.Date = DateFilter.Today);
        AddRadio(date.Items, "Past week", "Date", _options.Date == DateFilter.LastWeek, () => _options.Date = DateFilter.LastWeek);
        AddRadio(date.Items, "Past month", "Date", _options.Date == DateFilter.LastMonth, () => _options.Date = DateFilter.LastMonth);
        AddRadio(date.Items, "Past year", "Date", _options.Date == DateFilter.LastYear, () => _options.Date = DateFilter.LastYear);
        FilterMenu.Items.Add(date);

        AddToggle(FilterMenu.Items, "Show folders", _options.ShowFolders, on => _options.ShowFolders = on, "");
        FilterMenu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem clear = new() { Text = "Clear filters", IsEnabled = _options.IsFiltered, Icon = new FontIcon { Glyph = "" } };
        clear.Click += (_, _) => ClearFilters();
        FilterMenu.Items.Add(clear);
    }

    private void ViewMenu_Opening(object sender, object e)
    {
        ViewMenu.Items.Clear();
        AddRadio(ViewMenu.Items, "List", "View", _options.View == ViewMode.List, () => _options.View = ViewMode.List, "");
        AddRadio(ViewMenu.Items, "Tiles", "View", _options.View == ViewMode.Tiles, () => _options.View = ViewMode.Tiles, "");
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        AddToggle(ViewMenu.Items, "File name extensions", _options.ShowExtensions, on => _options.ShowExtensions = on);
        AddToggle(ViewMenu.Items, "Hidden items", _options.ShowHidden, on => _options.ShowHidden = on);
    }

    private void AddRadio(IList<MenuFlyoutItemBase> items, string text, string group, bool isChecked, Action apply, string? glyph = null)
    {
        RadioMenuFlyoutItem item = new() { Text = text, GroupName = group, IsChecked = isChecked };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        item.Click += (_, _) =>
        {
            apply();
            ApplyOptions();
        };
        items.Add(item);
    }

    private void AddToggle(IList<MenuFlyoutItemBase> items, string text, bool isChecked, Action<bool> apply, string? glyph = null)
    {
        ToggleMenuFlyoutItem item = new() { Text = text, IsChecked = isChecked };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            ApplyOptions();
        };
        items.Add(item);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ClearFilters();
    }

    private void ClearFilters()
    {
        _options.Kinds.Clear();
        _options.Date = DateFilter.Any;
        _options.ShowFolders = true;
        ApplyOptions();
    }

    private void ApplyOptions()
    {
        SettingsService.ViewOptions = _options;
        ApplyViewChrome();
        Arrange(force: true);
    }

    private void ApplyViewChrome()
    {
        bool tiles = _options.View == ViewMode.Tiles;
        FileList.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
        FileGrid.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        ViewGlyph.Glyph = tiles ? "" : "";

        // A filled glyph in the accent color says "some files are hidden by a filter".
        FilterGlyph.Visibility = _options.IsFiltered ? Visibility.Collapsed : Visibility.Visible;
        FilterOnGlyph.Visibility = _options.IsFiltered ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(FilterButton, _options.IsFiltered ? "Filter (on)" : "Filter");
    }
}
