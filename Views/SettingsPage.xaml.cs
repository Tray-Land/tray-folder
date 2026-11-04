using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayFolder.ViewModels;

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
}
