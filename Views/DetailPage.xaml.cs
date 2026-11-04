using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayFolder.Core;
using TrayFolder.Services;
using TrayFolder.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TrayFolder.Views;

/// <summary>
/// A file's preview and details. State and behavior live in <see cref="DetailViewModel"/>; this class
/// handles what only a view can: keyboard shortcuts, focus, drag and drop, and the media player.
/// </summary>
public sealed partial class DetailPage : Page, IFlyoutPage
{
    // Fallbacks for the preview area, and its margin, before its first layout.
    private const double DefaultPreviewWidth = 376;
    private const double DefaultPreviewHeight = 340;
    private const double PreviewMargin = 16;

    private readonly TrayFlyoutWindow _host;

    internal DetailPage(TrayFlyoutWindow host, IFileActions files, ITextProvider text, IErrorReporter errors)
    {
        _host = host;
        ViewModel = new DetailViewModel(new FilePreviewService(text, MeasurePreviewArea), files, host, text, errors);
        InitializeComponent();
        ViewModel.PauseMediaRequested += ViewModel_PauseMediaRequested;
    }

    public DetailViewModel ViewModel { get; }

    public void OnShown()
    {
        ViewModel.OnShown();
        BackButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden() => ViewModel.OnHidden();

    public void Dispose()
    {
        ViewModel.PauseMediaRequested -= ViewModel_PauseMediaRequested;
        ViewModel.Dispose();
    }

    private void ViewModel_PauseMediaRequested(object? sender, EventArgs e) => PreviewMedia.MediaPlayer?.Pause();

    private PreviewViewport MeasurePreviewArea()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double width = PreviewArea.ActualWidth > 0 ? PreviewArea.ActualWidth - PreviewMargin : DefaultPreviewWidth;
        double height = PreviewArea.ActualHeight > 0 ? PreviewArea.ActualHeight - PreviewMargin : DefaultPreviewHeight;
        return new PreviewViewport((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), scale);
    }

    private void Previous_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Slider)
        {
            return;
        }

        if (ViewModel.PreviousCommand.CanExecute(null))
        {
            ViewModel.PreviousCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void Next_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Slider)
        {
            return;
        }

        if (ViewModel.NextCommand.CanExecute(null))
        {
            ViewModel.NextCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void Copy_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Text selected in the preview copies as text, the usual way.
        if (PreviewText.SelectedText.Length > 0)
        {
            return;
        }

        args.Handled = true;
        if (ViewModel.CopyCommand.CanExecute(null))
        {
            ViewModel.CopyCommand.Execute(null);
        }
    }

    private void PreviewArea_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (ViewModel.Item is not { } item)
        {
            args.Cancel = true;
            return;
        }

        FileActions.FillDragData(args.Data, new[] { item }, item.Entry.Name);
        args.AllowedOperations = DataPackageOperation.Copy;
        args.DragUI.SetContentFromDataPackage();
        _host.SetDragging(true);
    }

    private void PreviewArea_DropCompleted(UIElement sender, DropCompletedEventArgs args) => _host.SetDragging(false);
}
