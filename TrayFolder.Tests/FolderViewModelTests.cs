using Microsoft.Extensions.Time.Testing;
using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

[TestClass]
public sealed class FolderViewModelTests
{
    private static FolderViewModel Create(TestFolders folders, FakeTimeProvider? time = null, TestErrors? errors = null,
        TestWatcher? watcher = null, TestFileActions? files = null, TestResources? resources = null, TestNavigation? navigation = null) =>
        new(folders, files ?? new TestFileActions(), resources ?? new TestResources(), watcher ?? new TestWatcher(),
            navigation ?? new TestNavigation(), new TestTextProvider(), time ?? TestTime.Create(), errors ?? new TestErrors());

    private static async Task<FolderViewModel> ShowAsync(TestFolders folders, FakeTimeProvider? time = null, TestErrors? errors = null,
        TestWatcher? watcher = null, TestFileActions? files = null, TestResources? resources = null, TestNavigation? navigation = null)
    {
        FolderViewModel vm = Create(folders, time, errors, watcher, files, resources, navigation);
        vm.OnShown();
        await vm.RefreshCommand.ExecutionTask!;
        return vm;
    }

    [TestMethod]
    public async Task Refresh_UnchangedEntries_PreservesCollectionRowsAndSelection()
    {
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders);
        IReadOnlyList<FileItemViewModel> items = vm.Items;
        FileItemViewModel row = vm.Items[0];
        vm.SelectedItems.Add(row);
        IReadOnlyList<FileGroupViewModel> groups = vm.Groups;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.AreSame(items, vm.Items);
        Assert.AreSame(row, vm.Items[0]);
        Assert.AreSame(groups, vm.Groups);
        Assert.AreSame(row, vm.SelectedItems.Single());
    }

    [TestMethod]
    public async Task Refresh_FileChanged_ReplacesOnlyThatRow()
    {
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders);
        FileItemViewModel unchanged = vm.Items.Single(i => i.Entry.Name == "a.txt");
        FileItemViewModel changed = vm.Items.Single(i => i.Entry.Name == "b.txt");

        folders.Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt", new DateTime(2026, 11, 3, 10, 0, 0))];
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.AreSame(unchanged, vm.Items.Single(i => i.Entry.Name == "a.txt"));
        Assert.AreNotSame(changed, vm.Items.Single(i => i.Entry.Name == "b.txt"));
    }

    [TestMethod]
    public async Task Refresh_OlderReadFinishesLast_PublishesOnlyNewestEntries()
    {
        TestFolders folders = new();
        TaskCompletionSource<IReadOnlyList<FileEntry>> old = new();
        TaskCompletionSource<IReadOnlyList<FileEntry>> recent = new();
        folders.PendingReads.Enqueue(old);
        folders.PendingReads.Enqueue(recent);
        using FolderViewModel vm = Create(folders);
        vm.OnShown();
        Task first = vm.RefreshCommand.ExecutionTask!;
        Task second = vm.RefreshCommand.ExecuteAsync(null);

        recent.SetResult([TestEntries.File("new.txt")]);
        await second;
        old.SetResult([TestEntries.File("old.txt")]);
        await first;

        Assert.IsTrue(folders.ReadTokens[0].IsCancellationRequested);
        Assert.AreEqual("new.txt", vm.Items.Single().Entry.Name);
        Assert.IsFalse(vm.RefreshCommand.IsRunning);
    }

    [TestMethod]
    public async Task OnHidden_ReadInFlight_CancelsWithoutPublishing()
    {
        TestFolders folders = new();
        TaskCompletionSource<IReadOnlyList<FileEntry>> pending = new();
        folders.PendingReads.Enqueue(pending);
        using FolderViewModel vm = Create(folders);
        vm.OnShown();
        Task read = vm.RefreshCommand.ExecutionTask!;

        vm.OnHidden();
        pending.SetResult([TestEntries.File("hidden.txt")]);
        await read;

        Assert.IsTrue(folders.ReadTokens.Single().IsCancellationRequested);
        Assert.AreEqual(0, vm.Items.Count);
        Assert.IsFalse(vm.RefreshCommand.IsRunning);
    }

    [TestMethod]
    public async Task OnShown_RootChangedWhileHidden_RefreshesNewRoot()
    {
        TestFolders folders = new();
        using FolderViewModel vm = await ShowAsync(folders);
        vm.OnHidden();
        folders.ReadPaths.Clear();
        folders.SetRoot(@"C:\other");

        vm.OnShown();
        await vm.RefreshCommand.ExecutionTask!;

        Assert.AreEqual(@"C:\other", vm.CurrentPath);
        Assert.AreEqual(@"C:\other", vm.RootPath);
        Assert.AreEqual(@"C:\other", folders.ReadPaths.Single());
    }

    [TestMethod]
    public void OnShown_AlreadyVisible_DoesNotReadAgain()
    {
        TestFolders folders = new();
        using FolderViewModel vm = Create(folders);

        vm.OnShown();
        vm.OnShown();

        Assert.AreEqual(1, folders.ReadPaths.Count);
    }

    [TestMethod]
    public async Task OnShownAndHidden_WatchesCurrentFolderOnlyWhileVisible()
    {
        TestWatcher watcher = new();
        TestFolders folders = new();
        using FolderViewModel vm = await ShowAsync(folders, watcher: watcher);
        Assert.AreEqual(@"C:\files", watcher.WatchedPath);
        Assert.IsTrue(folders.HasRootChangedSubscribers);

        vm.OnHidden();

        Assert.IsNull(watcher.WatchedPath);
        Assert.IsFalse(folders.HasRootChangedSubscribers);
    }

    [TestMethod]
    public async Task Activate_Folder_NavigatesAndWatchesIt()
    {
        TestWatcher watcher = new();
        TestFolders folders = new() { Entries = [TestEntries.Folder("sub")] };
        using FolderViewModel vm = await ShowAsync(folders, watcher: watcher);

        await vm.ActivateCommand.ExecuteAsync(vm.Items[0]);

        Assert.AreEqual(@"C:\files\sub", vm.CurrentPath);
        Assert.AreEqual(@"C:\files\sub", watcher.WatchedPath);
        CollectionAssert.AreEqual(new[] { "Files", "sub" }, vm.Breadcrumbs.ToArray());
        Assert.IsTrue(vm.HasBreadcrumbs);
    }

    [TestMethod]
    public async Task Activate_File_ShowsDetailWithFileSiblingsOnly()
    {
        TestNavigation navigation = new();
        TestFolders folders = new() { Entries = [TestEntries.Folder("sub"), TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, navigation: navigation);
        FileItemViewModel file = vm.Items.First(i => !i.IsFolder);

        await vm.ActivateCommand.ExecuteAsync(file);

        Assert.AreSame(file, navigation.DetailItem);
        Assert.AreEqual(2, navigation.DetailSiblings.Count);
    }

    [TestMethod]
    public async Task WatcherChanged_RereadsTheFolder()
    {
        TestWatcher watcher = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, watcher: watcher);

        folders.Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")];
        watcher.RaiseChanged();
        await vm.RefreshCommand.ExecutionTask!;

        Assert.AreEqual(2, vm.Items.Count);
    }

    [TestMethod]
    public async Task SearchText_Typing_AppliesOnlyAfterTheDelay()
    {
        FakeTimeProvider time = TestTime.Create();
        TestFolders folders = new() { Entries = [TestEntries.File("apple.txt"), TestEntries.File("pear.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, time);

        vm.SearchText = "app";
        Assert.AreEqual(2, vm.Items.Count);
        time.Advance(TimeSpan.FromMilliseconds(100));
        vm.SearchText = "appl";
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(2, vm.Items.Count, "A new keystroke restarts the delay.");

        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.AreEqual("apple.txt", vm.Items.Single().Entry.Name);
    }

    [TestMethod]
    public async Task Search_Command_AppliesImmediately()
    {
        TestFolders folders = new() { Entries = [TestEntries.File("apple.txt"), TestEntries.File("pear.txt")] };
        using FolderViewModel vm = await ShowAsync(folders);

        vm.SearchText = "pear";
        vm.SearchCommand.Execute(null);

        Assert.AreEqual("pear.txt", vm.Items.Single().Entry.Name);
    }

    [TestMethod]
    public async Task DayRollsOver_RowsKeepIdentityAndDetailsUpdate()
    {
        FakeTimeProvider time = TestTime.Create();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, time);
        FileItemViewModel row = vm.Items.Single();
        List<string?> notifications = [];
        row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        StringAssert.StartsWith(row.Details, "Today");

        time.Advance(TimeSpan.FromDays(1));

        Assert.AreSame(row, vm.Items.Single());
        StringAssert.StartsWith(row.Details, "Yesterday");
        CollectionAssert.Contains(notifications, nameof(FileItemViewModel.Details));
    }

    [TestMethod]
    public async Task OnHidden_StopsTheDayRolloverTimer()
    {
        FakeTimeProvider time = TestTime.Create();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, time);
        FileItemViewModel row = vm.Items.Single();
        vm.OnHidden();

        time.Advance(TimeSpan.FromDays(1));

        StringAssert.StartsWith(row.Details, "Today");
    }

    [TestMethod]
    public async Task FilterCommands_UpdateOptions_PreserveSearchUntilExplicitClear()
    {
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("a.png")] };
        using FolderViewModel vm = await ShowAsync(folders);
        vm.SearchText = "a";

        vm.FilterKindCommand.Execute(FileKind.Image);
        Assert.AreEqual("a.png", vm.Items.Single().Entry.Name);
        vm.ClearFiltersCommand.Execute(null);
        Assert.AreEqual("a", vm.SearchText);
        Assert.AreEqual(2, vm.Items.Count);
        vm.ClearSearchAndFiltersCommand.Execute(null);

        Assert.AreEqual(string.Empty, vm.SearchText);
        Assert.IsFalse(vm.IsFiltered);
        Assert.AreSame(vm.Options, folders.ViewOptions);
    }

    [TestMethod]
    public async Task FilterKind_TogglesAndReplacesTheList()
    {
        using FolderViewModel vm = await ShowAsync(new TestFolders());
        List<FileKind> before = vm.Options.Kinds;
        List<string?> notifications = [];
        vm.Options.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.FilterKindCommand.Execute(FileKind.Image);

        Assert.AreNotSame(before, vm.Options.Kinds);
        CollectionAssert.Contains(vm.Options.Kinds, FileKind.Image);
        CollectionAssert.Contains(notifications, nameof(FolderViewOptions.Kinds));

        vm.FilterKindCommand.Execute(FileKind.Image);

        Assert.AreEqual(0, vm.Options.Kinds.Count);
    }

    [TestMethod]
    public async Task OptionToggle_BoundDirectly_AppliesAndSaves()
    {
        TestFolders folders = new()
        {
            Entries = [TestEntries.File("seen.txt"), TestEntries.File("hidden.txt") with { IsHidden = true }],
            ViewOptions = new FolderViewOptions { GroupBy = GroupField.None },
        };
        using FolderViewModel vm = await ShowAsync(folders);
        Assert.AreEqual(1, vm.Items.Count);
        folders.ViewOptions = new FolderViewOptions();

        vm.Options.ShowHidden = true;

        Assert.AreEqual(2, vm.Items.Count);
        Assert.AreSame(vm.Options, folders.ViewOptions);
    }

    [TestMethod]
    public async Task Sort_NewField_PicksItsNaturalDirectionAndAppliesOnce()
    {
        using FolderViewModel vm = await ShowAsync(new TestFolders());
        int groupNotifications = 0;
        vm.PropertyChanged += (_, e) => groupNotifications += e.PropertyName == nameof(FolderViewModel.Groups) ? 1 : 0;

        vm.SortCommand.Execute(SortField.Name);

        Assert.AreEqual(SortField.Name, vm.Options.SortBy);
        Assert.IsFalse(vm.Options.Descending);
        Assert.AreEqual(1, groupNotifications);

        vm.SortCommand.Execute(SortField.Size);

        Assert.IsTrue(vm.Options.Descending);
    }

    [TestMethod]
    public async Task SetView_Tiles_NotifiesAndRebuildsItemSource()
    {
        using FolderViewModel vm = await ShowAsync(new TestFolders());
        List<string?> notifications = [];
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.SetViewCommand.Execute(ViewMode.Tiles);

        Assert.IsTrue(vm.IsTiles);
        Assert.AreEqual("\uECA5", vm.ViewGlyph);
        CollectionAssert.Contains(notifications, nameof(FolderViewModel.IsTiles));
        CollectionAssert.Contains(notifications, nameof(FolderViewModel.Groups));
    }

    [TestMethod]
    public async Task MenuCommands_TargetInsideSelection_UseWholeSelection()
    {
        TestFileActions files = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt"), TestEntries.File("c.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, files: files);
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);

        vm.MenuTarget = vm.Items[0];
        await vm.CopyTargetsCommand.ExecuteAsync(null);

        Assert.AreEqual(2, files.Copies.Single().Count);
        Assert.IsFalse(vm.CanPreviewTarget);
        Assert.IsFalse(vm.CanOpenTargetWith);
    }

    [TestMethod]
    public async Task MenuCommands_TargetOutsideSelection_UseOnlyThatRow()
    {
        TestFileActions files = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt"), TestEntries.File("c.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, files: files);
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);

        vm.MenuTarget = vm.Items[2];
        await vm.CopyTargetPathsCommand.ExecuteAsync(null);
        await vm.OpenTargetsCommand.ExecuteAsync(null);
        await vm.OpenTargetWithCommand.ExecuteAsync(null);
        await vm.ShowTargetInExplorerCommand.ExecuteAsync(null);

        Assert.AreSame(vm.Items[2].Entry, files.PathCopies.Single().Single());
        Assert.AreEqual(vm.Items[2].FullPath, files.Opens.Single());
        Assert.AreEqual(vm.Items[2].FullPath, files.OpenWiths.Single());
        Assert.AreEqual(vm.Items[2].FullPath, files.Shown.Single());
        Assert.IsTrue(vm.CanPreviewTarget);
        Assert.IsTrue(vm.CanOpenTargetWith);
    }

    [TestMethod]
    public async Task MenuTarget_Folder_OffersOpenHereAndNotOpenWith()
    {
        TestFolders folders = new() { Entries = [TestEntries.Folder("sub"), TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders);

        vm.MenuTarget = vm.Items.Single(i => i.IsFolder);
        Assert.AreEqual("Open folder here", vm.PreviewTargetText);
        Assert.IsFalse(vm.CanOpenTargetWith);

        vm.MenuTarget = vm.Items.Single(i => !i.IsFolder);
        Assert.AreEqual("Preview", vm.PreviewTargetText);
    }

    [TestMethod]
    public async Task SelectionCommands_FollowTheSelectionCount()
    {
        TestFileActions files = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, files: files);
        Assert.IsFalse(vm.CopySelectionCommand.CanExecute(null));
        Assert.IsFalse(vm.ActivateSelectionCommand.CanExecute(null));

        vm.SelectedItems.Add(vm.Items[0]);
        Assert.IsTrue(vm.ActivateSelectionCommand.CanExecute(null));

        vm.SelectedItems.Add(vm.Items[1]);
        Assert.IsFalse(vm.ActivateSelectionCommand.CanExecute(null));
        Assert.IsTrue(vm.CopySelectionCommand.CanExecute(null));
        StringAssert.Contains(vm.CountText, "2 selected");

        await vm.CopySelectionCommand.ExecuteAsync(null);

        Assert.AreEqual(2, files.Copies.Single().Count);
        Assert.AreEqual("Copied 2 items", vm.CountText);
    }

    [TestMethod]
    public async Task SelectionShortcuts_CutCopyPathsAndOpen_ActOnTheSelection()
    {
        TestFileActions files = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, files: files);
        Assert.IsFalse(vm.CutSelectionCommand.CanExecute(null));
        Assert.IsFalse(vm.CopySelectionPathsCommand.CanExecute(null));
        Assert.IsFalse(vm.OpenSelectionCommand.CanExecute(null));
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);

        await vm.CutSelectionCommand.ExecuteAsync(null);
        Assert.AreEqual(2, files.Cuts.Single().Count);
        Assert.AreEqual("Cut 2 items. Paste to move them.", vm.CountText);

        await vm.CopySelectionPathsCommand.ExecuteAsync(null);
        Assert.AreEqual(2, files.PathCopies.Single().Count);
        Assert.AreEqual("Copied 2 paths", vm.CountText);

        await vm.OpenSelectionCommand.ExecuteAsync(null);
        Assert.AreEqual(2, files.Opens.Count);
    }

    [TestMethod]
    public async Task ShowSelectionInExplorer_NeedsExactlyOneRow()
    {
        TestFileActions files = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, files: files);
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);
        Assert.IsFalse(vm.ShowSelectionInExplorerCommand.CanExecute(null));

        vm.SelectedItems.Remove(vm.Items[1]);
        await vm.ShowSelectionInExplorerCommand.ExecuteAsync(null);

        Assert.AreEqual(1, files.Shown.Count);
    }

    [TestMethod]
    public async Task SelectedItems_Added_PrefetchesForDrag()
    {
        TestResources resources = new();
        TestFolders folders = new() { Entries = [.. Enumerable.Range(0, 25).Select(i => TestEntries.File($"{i:00}.txt"))] };
        using FolderViewModel vm = await ShowAsync(folders, resources: resources);
        FileItemViewModel offscreen = vm.Items[^1];
        Assert.IsNull(offscreen.StorageItem, "Showing the page gets only the top rows ready.");

        vm.SelectedItems.Add(offscreen);

        Assert.IsNotNull(offscreen.StorageItem);
    }

    [TestMethod]
    public async Task OnShown_GetsTheTopRowsReadyToDrag()
    {
        TestResources resources = new();
        TestFolders folders = new() { Entries = [.. Enumerable.Range(0, 25).Select(i => TestEntries.File($"{i:00}.txt"))] };

        using FolderViewModel vm = await ShowAsync(folders, resources: resources);

        Assert.AreEqual(20, resources.Resolved.Count);
    }

    [TestMethod]
    public async Task LoadThumbnail_SizeFollowsTheView()
    {
        TestResources resources = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, resources: resources);

        vm.LoadThumbnail(vm.Items[0]);
        vm.SetViewCommand.Execute(ViewMode.Tiles);
        vm.LoadThumbnail(vm.Items[0]);

        CollectionAssert.AreEqual(new uint[] { 32, 96 }, resources.ThumbnailCalls.Select(c => c.Size).ToArray());
    }

    [TestMethod]
    public async Task DragTitle_NamesOneFileOrCountsSeveral()
    {
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] };
        using FolderViewModel vm = await ShowAsync(folders);

        Assert.AreEqual("a.txt", vm.DragTitle([vm.Items.Single(i => i.Entry.Name == "a.txt")]));
        Assert.AreEqual("2 items", vm.DragTitle(vm.Items));
    }

    [TestMethod]
    public async Task Groups_CarryHeaderAndCountForTheGroupHeader()
    {
        TestFolders folders = new()
        {
            Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")],
            ViewOptions = new FolderViewOptions { GroupBy = GroupField.Name },
        };
        using FolderViewModel vm = await ShowAsync(folders);

        FileGroupViewModel group = vm.Groups.Single();

        Assert.IsTrue(vm.IsGrouped);
        Assert.AreEqual("A - H", group.Header);
        Assert.AreEqual("2 items", group.CountText);
        Assert.AreEqual("A - H, 2 items", group.ToString());
    }

    [TestMethod]
    public async Task FileAction_Fails_ShowsStatusAndReportsError()
    {
        IOException error = new("No handler");
        TestErrors errors = new();
        TestFileActions files = new() { Error = error };
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        using FolderViewModel vm = await ShowAsync(folders, errors: errors, files: files);
        vm.MenuTarget = vm.Items[0];

        await vm.OpenTargetsCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsStatusOpen);
        Assert.AreEqual(error.Message, vm.StatusMessage);
        Assert.IsTrue(vm.HasError);
        Assert.AreSame(error, errors.Exceptions.Single());
    }

    [TestMethod]
    public async Task Refresh_ReadFails_ShowsStatusAndReportsError()
    {
        IOException error = new("Unavailable");
        TestFolders folders = new() { ReadError = error };
        TestErrors errors = new();
        using FolderViewModel vm = await ShowAsync(folders, errors: errors);

        Assert.IsTrue(vm.IsStatusOpen);
        Assert.IsFalse(vm.IsEmpty);
        Assert.AreEqual(error.Message, vm.StatusMessage);
        Assert.AreSame(error, errors.Exceptions.Single());
    }
}
