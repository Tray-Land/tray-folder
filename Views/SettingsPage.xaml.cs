using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayFolder.Services;
using Windows.ApplicationModel;

namespace TrayFolder.Views;

/// <summary>Settings as a page inside the flyout: the folder to show, recent folders, start with Windows.</summary>
public sealed partial class SettingsPage : Page
{
    private readonly TrayFlyoutWindow _host;
    private bool _loading = true;

    public SettingsPage(TrayFlyoutWindow host)
    {
        InitializeComponent();
        _host = host;
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
    }

    /// <summary>Re-reads everything: the startup task may have changed in Windows Settings meanwhile.</summary>
    public void OnShown()
    {
        ShowFolder();
        _ = LoadStartupStateAsync();
        BackButton.Focus(FocusState.Programmatic);
    }

    private void ShowFolder()
    {
        string root = FolderService.RootPath;
        FolderPathText.Text = root;
        ResetFolderButton.Visibility = string.Equals(root, FolderService.DownloadsPath, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

        int recent = SettingsService.RecentFolders.Count;
        RecentText.Text = recent == 0 ? "None yet." : recent == 1 ? "1 folder in the folder menu." : $"{recent} folders in the folder menu.";
        ClearRecentButton.IsEnabled = recent > 0;
    }

    private static string GetVersion()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "(unpackaged)";
        }
    }

    private async Task LoadStartupStateAsync() => ShowStartupState(await StartupService.GetStateAsync());

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
        }
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await _host.PickFolderAsync(FolderService.RootPath) is { } path)
        {
            FolderService.SetRoot(path);
            ShowFolder();
        }
    }

    private void ResetFolder_Click(object sender, RoutedEventArgs e)
    {
        FolderService.SetRoot(FolderService.DownloadsPath);
        ShowFolder();
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        FolderService.ClearRecent();
        ShowFolder();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.ShowMainPage();
}
