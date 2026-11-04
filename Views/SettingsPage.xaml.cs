using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayFolder.Core;
using TrayFolder.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace TrayFolder.Views;

public sealed partial class SettingsPage : Page, IFlyoutPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public void OnShown()
    {
        ViewModel.OnShown();
        BackButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden() => ViewModel.OnHidden();

    public void Dispose() => ViewModel.Dispose();

    // Backspace and Alt+Left go back to the list, unless the shortcut recorder is listening for keys.
    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsRecordingHotkey)
        {
            return;
        }

        if (ViewModel.BackCommand.CanExecute(null))
        {
            ViewModel.BackCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e) => ViewModel.StartHotkeyRecordingCommand.Execute(null);

    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e) => ViewModel.CancelHotkeyRecordingCommand.Execute(null);

    // Raw input only a view can read: turn the pressed combination into a gesture for the ViewModel.
    private void HotkeyButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecordingHotkey)
        {
            return;
        }

        VirtualKey key = e.Key;
        if (key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows or
            VirtualKey.RightWindows or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or
            VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            e.Handled = true; // wait for the key that completes the shortcut
            return;
        }

        e.Handled = true;
        if (key == VirtualKey.Escape)
        {
            ViewModel.CancelHotkeyRecordingCommand.Execute(null);
            return;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) { modifiers |= HotkeyModifiers.Control; }
        if (IsDown(VirtualKey.Menu)) { modifiers |= HotkeyModifiers.Alt; }
        if (IsDown(VirtualKey.Shift)) { modifiers |= HotkeyModifiers.Shift; }
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) { modifiers |= HotkeyModifiers.Windows; }

        ViewModel.SetHotkey(new HotkeyGesture(modifiers, (uint)key));
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
