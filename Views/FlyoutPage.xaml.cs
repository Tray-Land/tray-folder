using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using System.ComponentModel;
using System.Windows.Input;
using TrayFolder.Core;
using TrayFolder.Services;
using TrayFolder.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace TrayFolder.Views;

/// <summary>
/// The file list. State and behavior live in <see cref="FolderViewModel"/>; this class handles what
/// only a view can: raw input, focus, drag and drop, and which rows are on screen.
/// </summary>
public sealed partial class FlyoutPage : Page, IFlyoutPage
{
    private readonly TrayFlyoutWindow _host;
    private readonly HashSet<FileItemViewModel> _realizedItems = [];
    private bool _restoringSelection;
    private bool _focusListWhenFilled;

    internal FlyoutPage(TrayFlyoutWindow host, FolderViewModel viewModel)
    {
        _host = host;
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        foreach (ListViewBase view in new ListViewBase[] { FileList, FileGrid })
        {
            view.AddHandler(TappedEvent, new TappedEventHandler(Items_Tapped), handledEventsToo: true);
            view.AddHandler(PointerPressedEvent, new PointerEventHandler(Items_PointerPressed), handledEventsToo: true);
        }
    }

    public FolderViewModel ViewModel { get; }

    public FolderViewOptions Options => ViewModel.Options;

    private ListViewBase ActiveView => ViewModel.IsTiles ? FileGrid : FileList;

    public void OnShown()
    {
        ViewModel.OnShown();
        ActiveView.Focus(FocusState.Keyboard);

        // Showing re-reads the folder and rebuilds the list, which drops focus; put it back once the list fills.
        _focusListWhenFilled = true;

        // Coming back from the preview or settings doesn't rebuild the list, so also focus it as soon as it has laid out.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.IsVisible && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
            {
                ActiveView.Focus(FocusState.Keyboard);
            }
        });
        foreach (FileItemViewModel item in _realizedItems)
        {
            QueueThumbnail(item);
        }
    }

    public void OnHidden()
    {
        _focusListWhenFilled = false;
        ViewModel.OnHidden();
    }

    public void Dispose()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }

    public bool HandleEscape()
    {
        if (ViewModel.SearchText.Length == 0)
        {
            return false;
        }

        ViewModel.ClearSearchCommand.Execute(null);
        ActiveView.Focus(FocusState.Keyboard);
        return true;
    }

    private static FileItemViewModel? ItemFromSource(object source) =>
        (source as FrameworkElement)?.DataContext as FileItemViewModel;

    private static bool IsModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Runs a command if it can run now, and says whether it did.</summary>
    private static bool TryExecute(ICommand command, object? parameter = null)
    {
        bool canExecute = command.CanExecute(parameter);
        if (canExecute)
        {
            command.Execute(parameter);
        }

        return canExecute;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderViewModel.Groups))
        {
            ShowItems();
        }
    }

    /// <summary>
    /// Hands the rows to the visible view. Flat and grouped lists need different sources, and
    /// SelectedItems can't be bound, so this stays in code.
    /// </summary>
    private void ShowItems()
    {
        object source = ViewModel.IsGrouped
            ? new CollectionViewSource
            {
                Source = ViewModel.Groups,
                IsSourceGrouped = true,
                ItemsPath = new PropertyPath(nameof(FileGroupViewModel.Items)),
            }.View
            : ViewModel.Items;

        _restoringSelection = true;
        try
        {
            FileList.ItemsSource = ViewModel.IsTiles ? null : source;
            FileGrid.ItemsSource = ViewModel.IsTiles ? source : null;
            foreach (FileItemViewModel item in ViewModel.SelectedItems)
            {
                ActiveView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _restoringSelection = false;
        }

        if (_focusListWhenFilled && ViewModel.Items.Count > 0)
        {
            _focusListWhenFilled = false;

            // Leave focus alone if the user has already moved on, for example into the search box.
            if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ActiveView.Focus(FocusState.Keyboard));
            }
        }
    }

    private void Items_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource) is { } item && !IsModifierDown(VirtualKey.Control) && !IsModifierDown(VirtualKey.Shift))
        {
            TryExecute(ViewModel.ActivateCommand, item);
        }
    }

    private void Items_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource) is { } item)
        {
            ViewModel.Prefetch(item);
        }
    }

    private void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = e.Key switch
        {
            VirtualKey.C when IsModifierDown(VirtualKey.Control) && IsModifierDown(VirtualKey.Shift) =>
                TryExecute(ViewModel.CopySelectionPathsCommand),
            VirtualKey.C when IsModifierDown(VirtualKey.Control) => TryExecute(ViewModel.CopySelectionCommand),
            VirtualKey.X when IsModifierDown(VirtualKey.Control) => TryExecute(ViewModel.CutSelectionCommand),
            _ => false,
        };
    }

    // Preview, because the list consumes arrow keys before a plain KeyDown handler would see them.
    private void Items_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool plain = !IsModifierDown(VirtualKey.Control) && !IsModifierDown(VirtualKey.Shift) && !IsModifierDown(VirtualKey.Menu);
        e.Handled = e.Key switch
        {
            // Up from the top row continues to the search box instead of stopping at the list's edge.
            VirtualKey.Up when plain && IsOnFirstRow() => SearchBox.Focus(FocusState.Keyboard),
            // Down from the last row stays put; left to XY focus it would wrap around to the toolbar.
            VirtualKey.Down when plain && IsOnLastRow() => true,
            // Space toggles the focused row in or out of the selection, so several can be picked by keyboard.
            VirtualKey.Space when !IsModifierDown(VirtualKey.Menu) => ToggleFocusedItem(),
            // Enter is a click on the focused row: the row need not be selected first.
            VirtualKey.Enter when !IsModifierDown(VirtualKey.Control) => ActivateFocusedItem(),
            // Ctrl+Enter opens in the default app; Ctrl+Shift+E shows the file in Explorer.
            VirtualKey.Enter when IsModifierDown(VirtualKey.Control) => TryExecute(ViewModel.OpenSelectionCommand),
            VirtualKey.E when IsModifierDown(VirtualKey.Control) && IsModifierDown(VirtualKey.Shift) =>
                TryExecute(ViewModel.ShowSelectionInExplorerCommand),
            // Delete moves the focused row (or the selection it belongs to) to the Recycle Bin, after any warning.
            VirtualKey.Delete when plain => DeleteFocusedItem(),
            // The Menu key and Shift+F10 open the row menu, as a right-click does.
            VirtualKey.Application => ShowMenuForFocusedItem(),
            VirtualKey.F10 when IsModifierDown(VirtualKey.Shift) => ShowMenuForFocusedItem(),
            _ => false,
        };
    }

    // Up and Down between the toolbar rows. Left/Right inside a row is XY focus (see the XAML).
    // The columns of the two rows line up, so Up from Sort lands on Open in Explorer and so on.
    private void Toolbar_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down) ||
            FocusManager.GetFocusedElement(XamlRoot) is not UIElement focused ||
            focused is TextBox && e.Key == VirtualKey.Down)
        {
            return; // the search box's own handler takes Down
        }

        bool inSearchRow = ReferenceEquals(sender, SearchRow);
        if (e.Key == VirtualKey.Up)
        {
            if (inSearchRow)
            {
                (focused switch
                {
                    var f when ReferenceEquals(f, SortButton) => OpenFolderButton,
                    var f when ReferenceEquals(f, FilterButton) => PinButton,
                    var f when ReferenceEquals(f, ViewButton) => SettingsButton,
                    _ => (Control)FolderButton,
                }).Focus(FocusState.Keyboard);
            }

            e.Handled = true; // the top row has nothing above it
            return;
        }

        if (!inSearchRow)
        {
            SearchBox.Focus(FocusState.Keyboard);
        }
        else
        {
            FocusFirstItem(ActiveView.SelectedItem as FileItemViewModel);
        }

        e.Handled = true;
    }

    private void FocusFirstItem(FileItemViewModel? preferred)
    {
        if (ActiveView.Items.Count == 0)
        {
            return;
        }

        FileItemViewModel? item = preferred ?? ActiveView.Items[0] as FileItemViewModel;
        if (item is null)
        {
            return;
        }

        ActiveView.SelectedIndex = -1;
        ActiveView.SelectedItem = item;
        ActiveView.ScrollIntoView(item);
        ActiveView.Focus(FocusState.Keyboard);
    }

    /// <summary>Focus is on the top row of items, or on the first group header above it.</summary>
    private bool IsOnFirstRow()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not UIElement focused ||
            ActiveView.ContainerFromIndex(0) is not UIElement first)
        {
            return false;
        }

        // A group header sits above the first item; the first header is part of the top row.
        return focused is SelectorItem ? SameRow(focused, first) : IsAbove(focused, first);
    }

    private bool IsOnLastRow()
    {
        int count = ActiveView.Items.Count;
        return count > 0 &&
            FocusManager.GetFocusedElement(XamlRoot) is SelectorItem focused &&
            ActiveView.ContainerFromIndex(count - 1) is UIElement last &&
            SameRow(focused, last);
    }

    private double Top(UIElement element) =>
        element.TransformToVisual(ActiveView).TransformPoint(new Windows.Foundation.Point()).Y;

    private bool SameRow(UIElement a, UIElement b) => Math.Abs(Top(a) - Top(b)) < 1;

    private bool IsAbove(UIElement a, UIElement b) => Top(a) < Top(b);

    /// <summary>Previews or opens the focused row; with several rows selected and the focused one among them, opens them all.</summary>
    private bool ActivateFocusedItem()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not SelectorItem { Content: FileItemViewModel item })
        {
            return false;
        }

        if (ViewModel.SelectedItems.Count > 1 && ViewModel.SelectedItems.Contains(item))
        {
            return TryExecute(ViewModel.OpenSelectionCommand);
        }

        // Entering a folder rebuilds the list; keep the keyboard in it.
        _focusListWhenFilled = item.IsFolder;
        return TryExecute(ViewModel.ActivateCommand, item);
    }

    private bool ToggleFocusedItem()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not SelectorItem { Content: FileItemViewModel } container)
        {
            return false;
        }

        container.IsSelected = !container.IsSelected;
        return true;
    }

    private bool DeleteFocusedItem()
    {
        FileItemViewModel? item = FocusManager.GetFocusedElement(XamlRoot) is SelectorItem { Content: FileItemViewModel focused }
            ? focused
            : ViewModel.SelectedItems.FirstOrDefault();
        if (item is null)
        {
            return false;
        }

        // The list rebuilds after a delete; keep the keyboard in it.
        _focusListWhenFilled = true;
        ViewModel.MenuTarget = item;
        return TryExecute(ViewModel.DeleteTargetsCommand);
    }

    private void DeleteSwipe_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.DataContext is FileItemViewModel item)
        {
            ViewModel.MenuTarget = item;
            TryExecute(ViewModel.DeleteTargetsCommand);
        }
    }

    private void RecycleBin_DragOver(object sender, DragEventArgs e)
    {
        // Only rows dragged out of this list can be dropped here, not files from elsewhere.
        if (!ViewModel.IsDragging)
        {
            return;
        }

        // Copy is the one operation the drag offers (see FileActions.FillDragData); this target just reads it as "delete".
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = new ResourceLoader().GetString("DeleteConfirm");
        e.DragUIOverride.IsGlyphVisible = false;
        RecycleBinHot.Opacity = 1;
        e.Handled = true;
    }

    private void RecycleBin_DragLeave(object sender, DragEventArgs e) => RecycleBinHot.Opacity = 0;

    private void RecycleBin_Drop(object sender, DragEventArgs e)
    {
        RecycleBinHot.Opacity = 0;
        e.Handled = true;
        TryExecute(ViewModel.DeleteDraggedCommand);
    }

    private bool ShowMenuForFocusedItem()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not SelectorItem { Content: FileItemViewModel item } container)
        {
            return false;
        }

        ViewModel.MenuTarget = item;
        BuildItemMenu().ShowAt(container);
        return true;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            _focusListWhenFilled = ViewModel.GoUpCommand.CanExecute(null);
            TryExecute(ViewModel.GoUpCommand);
            e.Handled = true;
        }
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }

        ViewModel.SelectedItems.Clear();
        foreach (FileItemViewModel item in ActiveView.SelectedItems.OfType<FileItemViewModel>())
        {
            ViewModel.SelectedItems.Add(item);
        }
    }

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        List<FileItemViewModel> items = [.. e.Items.OfType<FileItemViewModel>()];
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        FileActions.FillDragData(e.Data, items, ViewModel.DragTitle(items));
        ViewModel.BeginDrag(items);
        _host.SetDragging(true);
    }

    private void Items_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.EndDrag();
        RecycleBinHot.Opacity = 0;
        _host.SetDragging(false);
    }

    private void Items_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FileItemViewModel item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            _realizedItems.Remove(item);
            return;
        }

        _realizedItems.Add(item);
        if (!ViewModel.IsVisible)
        {
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, Items_ContainerContentChanging);
            return;
        }

        QueueThumbnail(item);
    }

    private void QueueThumbnail(FileItemViewModel item) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ViewModel.LoadThumbnail(item));

    // The row menu is built fresh for each right-click. A single MenuFlyout shared by every row
    // (via ContextFlyout) could come back with no items after the page had been swapped out.
    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileItemViewModel item)
        {
            return;
        }

        ViewModel.MenuTarget = item;
        MenuFlyout menu = BuildItemMenu();
        if (args.TryGetPosition(sender, out Windows.Foundation.Point position))
        {
            menu.ShowAt(sender, new FlyoutShowOptions { Position = position });
        }
        else
        {
            menu.ShowAt((FrameworkElement)sender);
        }

        args.Handled = true;
    }

    private MenuFlyout BuildItemMenu()
    {
        ResourceLoader text = new();
        FolderViewModel vm = ViewModel;
        MenuFlyout menu = new();

        MenuFlyoutItem Item(string label, string glyph, ICommand command, string automationId, string? keys = null)
        {
            MenuFlyoutItem entry = new()
            {
                Text = label,
                Icon = new FontIcon { Glyph = glyph },
                Command = command,
            };
            AutomationProperties.SetAutomationId(entry, automationId);
            if (keys is not null)
            {
                entry.KeyboardAcceleratorTextOverride = keys;
            }

            return entry;
        }

        if (vm.CanPreviewTarget)
        {
            menu.Items.Add(Item(vm.PreviewTargetText, "", vm.PreviewTargetCommand, "PreviewMenuItem"));
        }

        menu.Items.Add(Item(text.GetString("MenuOpen/Text"), "", vm.OpenTargetsCommand, "OpenMenuItem", "Ctrl+Enter"));
        if (vm.CanOpenTargetWith)
        {
            menu.Items.Add(Item(text.GetString("MenuOpenWith/Text"), "", vm.OpenTargetWithCommand, "OpenWithRowMenuItem"));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(text.GetString("MenuCut/Text"), "", vm.CutTargetsCommand, "CutMenuItem", "Ctrl+X"));
        menu.Items.Add(Item(text.GetString("MenuCopy/Text"), "", vm.CopyTargetsCommand, "CopyMenuItem", "Ctrl+C"));
        menu.Items.Add(Item(text.GetString("MenuCopyPath/Text"), "", vm.CopyTargetPathsCommand, "CopyPathMenuItem", "Ctrl+Shift+C"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(text.GetString("MenuShowInExplorer/Text"), "", vm.ShowTargetInExplorerCommand, "ShowInExplorerMenuItem", "Ctrl+Shift+E"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(text.GetString("MenuDelete/Text"), "", vm.DeleteTargetsCommand, "DeleteMenuItem", "Del"));
        return menu;
    }

    private void FolderMenu_Opening(object sender, object e)
    {
        while (FolderMenu.Items[0] is RadioMenuFlyoutItem)
        {
            FolderMenu.Items.RemoveAt(0);
        }

        int index = 0;
        foreach (FolderChoice folder in ViewModel.FolderChoices)
        {
            RadioMenuFlyoutItem item = new()
            {
                Text = folder.Name,
                GroupName = "Folder",
                IsChecked = FolderPaths.Same(folder.Path, ViewModel.RootPath),
                Icon = new FontIcon { Glyph = folder.Glyph },
                Command = ViewModel.ChooseKnownFolderCommand,
                CommandParameter = folder.Path,
            };
            ToolTipService.SetToolTip(item, folder.Path);
            FolderMenu.Items.Insert(index++, item);
        }
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) =>
        TryExecute(ViewModel.BreadcrumbCommand, args.Index);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Down or VirtualKey.Enter)
        {
            ViewModel.SearchCommand.Execute(null);
            if (ViewModel.Items.Count > 0)
            {
                ActiveView.SelectedIndex = -1;
                ActiveView.SelectedItem = ViewModel.Items[0];
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
        _focusListWhenFilled = ViewModel.GoUpCommand.CanExecute(null);
        TryExecute(ViewModel.GoUpCommand);
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TryExecute(ViewModel.RefreshCommand);
        args.Handled = true;
    }
}
