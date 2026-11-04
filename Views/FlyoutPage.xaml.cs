using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using System.Windows.Input;
using TrayFolder.Core;
using TrayFolder.Services;
using TrayFolder.ViewModels;
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
        ActiveView.Focus(FocusState.Programmatic);
        foreach (FileItemViewModel item in _realizedItems)
        {
            QueueThumbnail(item);
        }
    }

    public void OnHidden() => ViewModel.OnHidden();

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
            VirtualKey.Enter => TryExecute(ViewModel.ActivateSelectionCommand),
            VirtualKey.C when IsModifierDown(VirtualKey.Control) => TryExecute(ViewModel.CopySelectionCommand),
            _ => false,
        };
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
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
        _host.SetDragging(true);
    }

    private void Items_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _host.SetDragging(false);

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

    private void ItemMenu_Opening(object sender, object e) =>
        ViewModel.MenuTarget = (sender as MenuFlyout)?.Target?.DataContext as FileItemViewModel;

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
        TryExecute(ViewModel.GoUpCommand);
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TryExecute(ViewModel.RefreshCommand);
        args.Handled = true;
    }
}
