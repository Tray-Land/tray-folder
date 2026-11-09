using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        // Enter in the list previewed this file; a second Enter opens it in its app. Back is Backspace or Alt+Left.
        OpenButton.Focus(FocusState.Keyboard);
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

    private DependencyObject? Focused => FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;

    private static bool IsWithin(DependencyObject? element, DependencyObject container)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, container))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Left/Right belong to the focused control when it is a text box, a slider or one of the action buttons.</summary>
    private bool FocusOwnsHorizontalKeys => Focused is TextBox or Slider || IsWithin(Focused, ActionRow);

    /// <summary>A button already turns Enter and Space into a click; a page shortcut on top of that would act twice.</summary>
    private bool FocusIsButton => Focused is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase;

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void Previous_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusOwnsHorizontalKeys)
        {
            return;
        }

        if (ViewModel.PreviousCommand.CanExecute(null))
        {
            ViewModel.PreviousCommand.Execute(null);
        }

        args.Handled = true;
    }

    // Backspace and Alt+Left go back to the list, as in Explorer.
    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        if (ViewModel.BackCommand.CanExecute(null))
        {
            ViewModel.BackCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void Next_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusOwnsHorizontalKeys)
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

    private void Cut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(ViewModel.CutCommand);
    }

    private void CopyPath_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(ViewModel.CopyPathsCommand);
    }

    private void ShowInExplorer_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(ViewModel.ShowInExplorerCommand);
    }

    private void OpenWith_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(ViewModel.OpenWithCommand);
    }

    // Enter opens the file in its app, unless a button has focus and is about to handle the Enter itself.
    private void Open_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        bool ctrl = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        if (FocusIsButton && !ctrl)
        {
            return;
        }

        args.Handled = true;
        Run(ViewModel.OpenCommand);
    }

    // The Menu key and Shift+F10 open the More menu, as a right-click would.
    private void MoreMenu_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoreButton.Flyout?.ShowAt(MoreButton);
    }

    private void PreviousPage_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ViewModel.PreviousPageCommand.CanExecute(null);
        Run(ViewModel.PreviousPageCommand);
    }

    private void NextPage_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ViewModel.NextPageCommand.CanExecute(null);
        Run(ViewModel.NextPageCommand);
    }

    // Up and Down hop between the header buttons and the action buttons; the preview between them is content.
    private void FocusHeader_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsWithin(Focused, ActionRow) && Focused is not Slider)
        {
            args.Handled = BackButton.Focus(FocusState.Keyboard);
        }
    }

    private void FocusActions_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsWithin(Focused, ActionRow) && Focused is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            args.Handled = OpenButton.Focus(FocusState.Keyboard);
        }
    }

    // Space plays or pauses the preview's audio or video when no button is focused.
    private void PlayPause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusIsButton || !ViewModel.Preview.HasMedia || PreviewMedia.MediaPlayer is not { } player)
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
        {
            player.Pause();
        }
        else
        {
            player.Play();
        }

        args.Handled = true;
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
