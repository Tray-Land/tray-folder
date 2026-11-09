using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using TrayFolder.Core;

namespace TrayFolder.ViewModels;

public sealed partial class FolderViewModel : VisibleViewModel
{
    private const uint ListThumbnailSize = 32;
    private const uint TileThumbnailSize = 96;
    private const int PrefetchedRows = 20;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the "Undo delete" pill stays up.</summary>
    internal static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    private readonly IFolderRepository _folders;
    private readonly IFileActions _files;
    private readonly IFileDeleter _deleter;
    private readonly IFileResources _resources;
    private readonly IFolderWatcher _watcher;
    private readonly IFlyoutNavigation _navigation;
    private readonly TimeProvider _time;
    private readonly LatestRequest _refresh = new();
    private readonly LatestRequest _searchDelay = new();
    private readonly LatestRequest _deleteNotice = new();
    private IReadOnlyList<DeletedItem> _undoable = [];
    private IReadOnlyList<FileItemViewModel> _dragged = [];
    private Dictionary<string, FileItemViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FileEntry> _entries = [];
    private bool _updatingOptions;

    public FolderViewModel(IFolderRepository folders, IFileActions files, IFileDeleter deleter, IFileResources resources,
        IFolderWatcher watcher, IFlyoutNavigation navigation, ITextProvider text, TimeProvider time, IErrorReporter errors)
        : base(text, errors)
    {
        _folders = folders;
        _files = files;
        _deleter = deleter;
        _resources = resources;
        _watcher = watcher;
        _navigation = navigation;
        _time = time;
        Options = folders.ViewOptions;
        RootPath = folders.RootPath;
        CurrentPath = RootPath;
        Options.PropertyChanged += Options_PropertyChanged;
        SelectedItems.CollectionChanged += (_, e) => SelectionChanged(e.NewItems);
        _watcher.Changed += (_, _) => _ = RefreshCommand.ExecuteAsync(null);
        UpdateHeader();
    }

    public ObservableCollection<FileItemViewModel> SelectedItems { get; } = [];

    public FolderViewOptions Options { get; }

    public bool IsTiles => Options.View == ViewMode.Tiles;

    public bool IsGrouped => Options.GroupBy != GroupField.None;

    public bool IsFiltered => Options.IsFiltered;

    public string ViewGlyph => IsTiles ? "" : "";

    public string FilterTip => Text.Get(IsFiltered ? "FilterOn" : "Filter");

    public bool HasBreadcrumbs => Breadcrumbs.Count > 0;

    public bool CanPreviewTarget => Targets.Count == 1;

    public bool CanOpenTargetWith => CanPreviewTarget && MenuTarget is { IsFolder: false };

    public string PreviewTargetText => Text.Get(MenuTarget is { IsFolder: true } ? "OpenFolderHere" : "Preview");

    /// <summary>What a context-menu action applies to: the whole selection if the menu opened on a part of it.</summary>
    private List<FileItemViewModel> Targets =>
        MenuTarget is null ? [] : SelectedItems.Contains(MenuTarget) ? [.. SelectedItems] : [MenuTarget];

    private bool CanActivateSelection => SelectedItems.Count == 1;

    private bool CanCopySelection => SelectedItems.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<FileItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FileGroupViewModel> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial string RootPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderGlyph { get; private set; } = FolderChoice.DefaultGlyph;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Pinned: light-dismiss is off, so several files can be dragged out one after another.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool CanClearFilters { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusOpen { get; private set; }

    [ObservableProperty]
    public partial bool CanChooseFolder { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBreadcrumbs))]
    public partial IReadOnlyList<string> Breadcrumbs { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FolderChoice> FolderChoices { get; private set; } = [];

    /// <summary>A drag out of the list is under way, so the Recycle Bin drop target shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteNoticeShown))]
    public partial bool IsDragging { get; private set; }

    /// <summary>The "Deleted · Undo delete" pill is up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteNoticeShown))]
    public partial bool IsDeleteNoticeOpen { get; private set; }

    /// <summary>The pill gives its place to the Recycle Bin target while a drag is under way.</summary>
    public bool IsDeleteNoticeShown => IsDeleteNoticeOpen && !IsDragging;

    [ObservableProperty]
    public partial string DeleteNoticeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanUndoDelete { get; private set; }

    /// <summary>The row the context menu last opened on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviewTarget), nameof(CanOpenTargetWith), nameof(PreviewTargetText))]
    public partial FileItemViewModel? MenuTarget { get; set; }

    /// <summary>Loads a row's thumbnail at the size the current view shows it.</summary>
    public void LoadThumbnail(FileItemViewModel item) =>
        _ = item.LoadThumbnailAsync(IsTiles ? TileThumbnailSize : ListThumbnailSize, VisibleToken);

    /// <summary>Gets a row ready to drag; a press may be the start of one.</summary>
    public void Prefetch(FileItemViewModel item) => _ = item.PrefetchAsync(VisibleToken);

    public string DragTitle(IReadOnlyList<FileItemViewModel> items) =>
        items.Count == 1 ? items[0].Entry.Name : CountOf(items.Count);

    /// <summary>A drag out of the list started; dropping on the Recycle Bin target deletes these rows.</summary>
    public void BeginDrag(IReadOnlyList<FileItemViewModel> items)
    {
        _dragged = items;
        IsDragging = true;
    }

    public void EndDrag()
    {
        _dragged = [];
        IsDragging = false;
    }

    protected override void OnActivated()
    {
        _folders.RootChanged += RootChanged;
        if (!FolderPaths.Same(RootPath, _folders.RootPath))
        {
            RootPath = _folders.RootPath;
            Navigate(RootPath);
        }

        _watcher.Watch(CurrentPath);
        Arrange();
        _ = RefreshDatesAtMidnightAsync(VisibleToken);
        _ = RefreshCommand.ExecuteAsync(null);
        foreach (FileItemViewModel item in Items.Take(PrefetchedRows))
        {
            Prefetch(item);
        }
    }

    protected override void OnDeactivated()
    {
        _folders.RootChanged -= RootChanged;
        _watcher.Stop();

        // Deleted files stay in the Recycle Bin; only the quick undo goes.
        DismissDeleteNotice();
        EndDrag();
    }

    protected override void OnFailureReported(Exception exception) =>
        ShowStatus(Text.Get("FileActionFailed"), exception.Message, canChoose: false);

    partial void OnCurrentPathChanged(string value)
    {
        if (IsVisible)
        {
            _watcher.Watch(value);
        }
    }

    partial void OnSearchTextChanged(string value) => _ = ArrangeAfterSearchDelayAsync();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (!IsVisible)
        {
            return;
        }

        using LatestRequest.Scope request = _refresh.Begin(VisibleToken);
        CancellationToken token = request.Token;
        string path = CurrentPath;
        try
        {
            IReadOnlyList<FolderChoice> choices = await _folders.GetFolderChoicesAsync(token);
            token.ThrowIfCancellationRequested();
            FolderChoices = choices;
            UpdateHeader();
            IReadOnlyList<FileEntry> entries = await _folders.EnumerateAsync(path, token);
            token.ThrowIfCancellationRequested();
            IsStatusOpen = false;
            _entries = entries;
            Arrange();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (DirectoryNotFoundException ex) when (!token.IsCancellationRequested)
        {
            if (!FolderPaths.Same(path, RootPath) && _folders.DirectoryExists(RootPath))
            {
                await NavigateAndRefreshAsync(RootPath);
            }
            else
            {
                ReportFailure("Read folder", ex);
                ShowStatus(Text.Get("FolderMissing"), Text.Format("FolderMoved", path), canChoose: true);
                _entries = [];
                Arrange();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Read folder", ex, token);
            if (!token.IsCancellationRequested)
            {
                ShowStatus(Text.Get("FolderUnreadable"), ex.Message, FolderPaths.Same(path, RootPath));
            }
        }
    }

    /// <summary>Applies the search now rather than after the typing delay.</summary>
    [RelayCommand]
    private void Search() => Arrange();

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        Arrange();
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (!FolderPaths.Same(CurrentPath, RootPath) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(CurrentPath)) is { } parent)
        {
            await NavigateAndRefreshAsync(parent);
        }
    }

    [RelayCommand]
    private Task BreadcrumbAsync(int index)
    {
        string path = RootPath;
        foreach (string segment in Breadcrumbs.Skip(1).Take(index))
        {
            path = Path.Combine(path, segment);
        }

        return NavigateAndRefreshAsync(path);
    }

    [RelayCommand]
    private Task ActivateAsync(FileItemViewModel item)
    {
        if (item.IsFolder)
        {
            return NavigateAndRefreshAsync(item.FullPath);
        }

        _navigation.ShowDetailPage(item, Items.Where(i => !i.IsFolder).ToList());
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanActivateSelection))]
    private Task ActivateSelectionAsync() => ActivateAsync(SelectedItems[0]);

    [RelayCommand]
    private void Settings() => _navigation.ShowSettingsPage();

    [RelayCommand]
    private Task ChooseFolderAsync() => RunAsync("Choose folder", async token =>
    {
        string? path = await _navigation.PickFolderAsync(RootPath, token);
        token.ThrowIfCancellationRequested();
        if (path is not null)
        {
            _folders.SetRoot(path);
        }
    });

    [RelayCommand]
    private Task ChooseKnownFolderAsync(string path) => RunAsync("Choose folder", _ =>
    {
        if (!FolderPaths.Same(path, RootPath))
        {
            _folders.SetRoot(path);
        }
        else if (!FolderPaths.Same(CurrentPath, RootPath))
        {
            return NavigateAndRefreshAsync(RootPath);
        }

        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenFolderAsync() => RunFileActionAsync(token => _files.OpenFolderAsync(CurrentPath, token));

    [RelayCommand]
    private Task PreviewTargetAsync() => MenuTarget is { } target ? ActivateAsync(target) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenTargetsAsync() => OpenAsync(Targets);

    private Task OpenAsync(List<FileItemViewModel> items) => RunFileActionAsync(async token =>
    {
        foreach (FileItemViewModel item in items)
        {
            await _files.OpenAsync(item.FullPath, token);
        }
    });

    [RelayCommand]
    private Task OpenTargetWithAsync() =>
        MenuTarget is { } target ? RunFileActionAsync(token => _files.OpenWithAsync(target.FullPath, token)) : Task.CompletedTask;

    [RelayCommand]
    private Task ShowTargetInExplorerAsync() =>
        MenuTarget is { } target ? RunFileActionAsync(token => _files.ShowInExplorerAsync(target.FullPath, token)) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyTargetsAsync() => CopyAsync(Targets);

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task CopySelectionAsync() => CopyAsync([.. SelectedItems]);

    [RelayCommand]
    private Task CutTargetsAsync() => CutAsync(Targets);

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task CutSelectionAsync() => CutAsync([.. SelectedItems]);

    [RelayCommand]
    private Task CopyTargetPathsAsync() => CopyPathsAsync(Targets);

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task CopySelectionPathsAsync() => CopyPathsAsync([.. SelectedItems]);

    /// <summary>Opens the selection in each file's default app (Enter previews a single one inside the flyout).</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task OpenSelectionAsync() => OpenAsync([.. SelectedItems]);

    [RelayCommand(CanExecute = nameof(CanActivateSelection))]
    private Task ShowSelectionInExplorerAsync() =>
        RunFileActionAsync(token => _files.ShowInExplorerAsync(SelectedItems[0].FullPath, token));

    [RelayCommand]
    private void Sort(SortField field) => UpdateOptions(() =>
    {
        if (Options.SortBy != field)
        {
            Options.Descending = field is SortField.DateModified or SortField.Size;
        }

        Options.SortBy = field;
    });

    [RelayCommand]
    private void SortAscending() => Options.Descending = false;

    [RelayCommand]
    private void SortDescending() => Options.Descending = true;

    [RelayCommand]
    private void Group(GroupField value) => Options.GroupBy = value;

    [RelayCommand]
    private void FilterKind(FileKind value) =>
        Options.Kinds = Options.Kinds.Contains(value) ? [.. Options.Kinds.Where(k => k != value)] : [.. Options.Kinds, value];

    [RelayCommand]
    private void FilterDate(DateFilter value) => Options.Date = value;

    [RelayCommand]
    private void SetView(ViewMode value) => Options.View = value;

    [RelayCommand]
    private void ClearFilters() => UpdateOptions(() =>
    {
        Options.Kinds = [];
        Options.Date = DateFilter.Any;
        Options.ShowFolders = true;
    });

    [RelayCommand]
    private void ClearSearchAndFilters()
    {
        SearchText = string.Empty;
        ClearFilters();
    }

    /// <summary>Deletes the context menu's target rows (the whole selection if it opened on a selected row).</summary>
    [RelayCommand]
    private Task DeleteTargetsAsync() => DeleteAsync(Targets);

    /// <summary>Deletes the rows being dragged, when they are dropped on the Recycle Bin target.</summary>
    [RelayCommand]
    private Task DeleteDraggedAsync() => DeleteAsync([.. _dragged]);

    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        IReadOnlyList<DeletedItem> items = _undoable;
        DismissDeleteNotice();
        if (items.Count == 0)
        {
            return;
        }

        await RunFileActionAsync(async token =>
        {
            int restored = await _deleter.RestoreAsync(items, token);
            await RefreshCommand.ExecuteAsync(null);
            token.ThrowIfCancellationRequested();
            if (restored < items.Count)
            {
                ShowStatus(Text.Get("RestoreIncompleteTitle"), Text.Format("RestoreIncomplete", items.Count - restored), canChoose: false);
            }
            else
            {
                CountText = Text.Format(restored == 1 ? "RestoredItem" : "RestoredItems", restored);
            }
        });
    }

    [RelayCommand]
    private void DismissDelete() => DismissDeleteNotice();

    private Task DeleteAsync(List<FileItemViewModel> items) =>
        items.Count == 0 || DeleteItemsCommand.IsRunning ? Task.CompletedTask : DeleteItemsCommand.ExecuteAsync(items);

    // Nothing is touched until the user has answered any warning. Not canceled by hiding once the
    // shell has started: a half-finished delete helps nobody.
    [RelayCommand]
    private Task DeleteItemsAsync(List<FileItemViewModel> items) => RunFileActionAsync(async token =>
    {
        List<FileEntry> entries = items.ConvertAll(i => i.Entry);
        DeleteAssessment assessment = DeletePolicy.Assess(entries);
        if (assessment.RequiresConfirmation && !await _navigation.ConfirmAsync(ConfirmationFor(entries, assessment), token))
        {
            return;
        }

        token.ThrowIfCancellationRequested();
        DeleteOutcome outcome;
        using (_navigation.KeepOpen())
        {
            outcome = await _deleter.DeleteAsync(entries, CancellationToken.None);
        }

        await RefreshCommand.ExecuteAsync(null);
        if (IsVisible)
        {
            ShowDeleteNotice(outcome);
        }
    });

    private ConfirmRequest ConfirmationFor(List<FileEntry> entries, DeleteAssessment assessment)
    {
        List<string> reasons = [];
        void Reason(DeleteConcern concern, string key, params object[] arguments)
        {
            if (assessment.Concerns.HasFlag(concern))
            {
                reasons.Add(Text.Format(key, arguments));
            }
        }

        Reason(DeleteConcern.ManyItems, "DeleteWarnMany", assessment.Count);
        Reason(DeleteConcern.Folders, "DeleteWarnFolders", assessment.Folders);
        Reason(DeleteConcern.Programs, "DeleteWarnPrograms", assessment.Programs);
        Reason(DeleteConcern.SystemFiles, "DeleteWarnSystem", assessment.SystemFiles);
        Reason(DeleteConcern.ReadOnlyFiles, "DeleteWarnReadOnly", assessment.ReadOnlyFiles);
        Reason(DeleteConcern.LargeSize, "DeleteWarnLarge", FolderViewPolicy.FormatSize(assessment.TotalSize));
        reasons.Add(Text.Get("DeleteWarnRecycle"));

        string title = entries.Count == 1 ? Text.Format("DeleteConfirmOne", entries[0].Name) : Text.Format("DeleteConfirmMany", entries.Count);
        return new ConfirmRequest(title, string.Join(Environment.NewLine, reasons), Text.Get("DeleteConfirm"), Text.Get("Cancel"));
    }

    private void ShowDeleteNotice(DeleteOutcome outcome)
    {
        int deleted = outcome.Recycled.Count + outcome.Gone;
        if (deleted == 0)
        {
            return;
        }

        _undoable = outcome.Recycled;
        CanUndoDelete = outcome.Recycled.Count > 0;
        DeleteNoticeText = deleted == 1 && outcome.Recycled.Count == 1
            ? Text.Format("DeletedOne", outcome.Recycled[0].Entry.Name)
            : Text.Format("DeletedMany", deleted);
        IsDeleteNoticeOpen = true;
        _ = HideDeleteNoticeAfterDelayAsync();
    }

    private async Task HideDeleteNoticeAfterDelayAsync()
    {
        using LatestRequest.Scope request = _deleteNotice.Begin(VisibleToken);
        try
        {
            await Task.Delay(UndoWindow, _time, request.Token);
            DismissDeleteNotice();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
    }

    private void DismissDeleteNotice()
    {
        _deleteNotice.Cancel();
        _undoable = [];
        CanUndoDelete = false;
        IsDeleteNoticeOpen = false;
    }

    private Task CopyAsync(List<FileItemViewModel> items) => RunFileActionAsync(async token =>
    {
        await _files.CopyAsync(items.ConvertAll(i => i.Entry), token);
        token.ThrowIfCancellationRequested();
        CountText = Text.Format(items.Count == 1 ? "CopiedItem" : "CopiedItems", items.Count);
    });

    private Task CutAsync(List<FileItemViewModel> items) => RunFileActionAsync(async token =>
    {
        await _files.CutAsync(items.ConvertAll(i => i.Entry), token);
        token.ThrowIfCancellationRequested();
        CountText = Text.Format(items.Count == 1 ? "CutItem" : "CutItems", items.Count);
    });

    private Task CopyPathsAsync(List<FileItemViewModel> items)
    {
        List<FileEntry> entries = [.. items.Select(i => i.Entry)];
        return RunFileActionAsync(async token =>
        {
            await _files.CopyPathsAsync(entries, token);
            token.ThrowIfCancellationRequested();
            CountText = Text.Format(entries.Count == 1 ? "CopiedPath" : "CopiedPaths", entries.Count);
        });
    }

    private Task RunFileActionAsync(Func<CancellationToken, Task> action) => RunAsync("File action", action);

    private void RootChanged(object? sender, EventArgs e)
    {
        RootPath = _folders.RootPath;
        _ = NavigateAndRefreshAsync(RootPath);
    }

    private Task NavigateAndRefreshAsync(string path)
    {
        Navigate(path);
        return RefreshCommand.ExecuteAsync(null);
    }

    private void Navigate(string path)
    {
        _refresh.Cancel();
        CurrentPath = path;
        _entries = [];
        _rows.Clear();
        SelectedItems.Clear();
        SearchText = string.Empty;
        IsStatusOpen = false;
        UpdateHeader();
        Arrange();
    }

    private void SelectionChanged(System.Collections.IList? added)
    {
        UpdateCount();
        OnPropertyChanged(nameof(CanPreviewTarget));
        OnPropertyChanged(nameof(CanOpenTargetWith));
        foreach (FileItemViewModel item in added ?? Array.Empty<FileItemViewModel>())
        {
            Prefetch(item);
        }
    }

    private async Task ArrangeAfterSearchDelayAsync()
    {
        if (!IsVisible)
        {
            return;
        }

        using LatestRequest.Scope request = _searchDelay.Begin(VisibleToken);
        try
        {
            await Task.Delay(SearchDelay, _time, request.Token);
            Arrange();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
    }

    /// <summary>Relative dates ("Today 9:30") go stale when the day rolls over with the flyout open.</summary>
    private async Task RefreshDatesAtMidnightAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                DateTime now = _time.GetLocalNow().DateTime;
                await Task.Delay(now.Date.AddDays(1) - now + TimeSpan.FromSeconds(1), _time, token);
                Arrange();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Publishes the rows for the current entries, options, search and date.</summary>
    private void Arrange()
    {
        _searchDelay.Cancel();
        DateTime now = _time.GetLocalNow().DateTime;
        Dictionary<string, FileItemViewModel> rows = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry entry in _entries)
        {
            // Keep a row while its file is unchanged, so it keeps its thumbnail across refreshes.
            FileItemViewModel row = _rows.TryGetValue(entry.FullPath, out FileItemViewModel? existing) && existing.Entry == entry
                ? existing
                : new FileItemViewModel(entry, now, Text, _resources, Errors);
            row.ShowExtension = Options.ShowExtensions;
            row.RefreshDate(now);
            rows[entry.FullPath] = row;
        }

        _rows = rows;
        List<FileGroupViewModel> groups = [.. FolderViewPolicy.Arrange(
                _entries, Options, SearchText, now, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek)
            .Select(g => new FileGroupViewModel(g.Header, [.. g.Items.Select(e => rows[e.FullPath])], CountOf(g.Items.Count)))];
        List<FileItemViewModel> visible = [.. groups.SelectMany(g => g.Items)];
        bool changed = !visible.SequenceEqual(Items) || !groups.Select(g => (g.Header, g.Items.Count))
            .SequenceEqual(Groups.Select(g => (g.Header, g.Items.Count)));
        if (changed)
        {
            HashSet<string> selected = SelectedItems.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Items = visible;
            SelectedItems.Clear();
            foreach (FileItemViewModel item in visible.Where(i => selected.Contains(i.FullPath)))
            {
                SelectedItems.Add(item);
            }

            Groups = groups;
        }

        UpdateEmptyState();
        UpdateCount();
    }

    private void Options_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingOptions)
        {
            ApplyOptions();
        }
    }

    /// <summary>Changes several options as one, so they save and re-sort once.</summary>
    private void UpdateOptions(Action change)
    {
        _updatingOptions = true;
        try
        {
            change();
        }
        finally
        {
            _updatingOptions = false;
        }

        ApplyOptions();
    }

    private void ApplyOptions() => Run("Save view options", () =>
    {
        _folders.ViewOptions = Options;
        OnPropertyChanged(nameof(IsTiles));
        OnPropertyChanged(nameof(ViewGlyph));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterTip));
        Arrange();

        // A view or grouping switch needs a new item source even when the same rows remain visible.
        OnPropertyChanged(nameof(Groups));
    });

    private void UpdateHeader()
    {
        FolderChoice? choice = FolderChoices.FirstOrDefault(c => FolderPaths.Same(c.Path, RootPath));
        FolderTitle = choice?.Name ?? FolderPaths.LeafName(RootPath);
        FolderGlyph = choice?.Glyph ?? FolderChoice.DefaultGlyph;
        Breadcrumbs = FolderPaths.Same(CurrentPath, RootPath) ? Array.Empty<string>() :
            [
                FolderTitle,
                .. Path.GetRelativePath(RootPath, CurrentPath)
                        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries),
            ];
    }

    private void UpdateEmptyState()
    {
        IsEmpty = Items.Count == 0 && !IsStatusOpen;
        EmptyText = Text.Get(_entries.Count == 0 ? "FolderEmpty" : "NothingMatches");
        CanClearFilters = IsEmpty && _entries.Count > 0 &&
            (SearchText.Length > 0 || IsFiltered || (!Options.ShowHidden && _entries.Any(e => e.IsHidden)));
    }

    private void UpdateCount()
    {
        int total = _entries.Count(e => Options.ShowHidden || !e.IsHidden);
        string count = Items.Count == total ? CountOf(total) : Text.Format("FilteredCount", Items.Count, total);
        CountText = SelectedItems.Count > 1 ? Text.Format("SelectedCount", count, SelectedItems.Count) : count;
    }

    private string CountOf(int count) => Text.Format(count == 1 ? "ItemCount" : "ItemsCount", count);

    private void ShowStatus(string title, string message, bool canChoose)
    {
        StatusTitle = title;
        StatusMessage = message;
        CanChooseFolder = canChoose;
        IsStatusOpen = true;
        UpdateEmptyState();
    }
}
