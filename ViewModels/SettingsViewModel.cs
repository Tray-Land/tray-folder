using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using TrayFolder.Core;

namespace TrayFolder.ViewModels;

public sealed partial class SettingsViewModel : VisibleViewModel
{
    private readonly IFolderRepository _folders;
    private readonly IStartupService _startup;
    private readonly IFlyoutNavigation _navigation;
    private readonly LatestRequest _startupRequest = new();
    private StartupState _startupState;

    public SettingsViewModel(IFolderRepository folders, IStartupService startup, IFlyoutNavigation navigation,
        ITextProvider text, IErrorReporter errors, string version) : base(text, errors)
    {
        _folders = folders;
        _startup = startup;
        _navigation = navigation;
        Version = version;
        LoadStartupCommand.PropertyChanged += StartupCommand_PropertyChanged;
        SetStartupCommand.PropertyChanged += StartupCommand_PropertyChanged;
    }

    public string Version { get; }

    /// <summary>The toggle waits for any read or write, including a write still finishing from before the page hid.</summary>
    public bool CanChangeStartup => !LoadStartupCommand.IsRunning && !SetStartupCommand.IsRunning &&
        _startupState is StartupState.Enabled or StartupState.Disabled;

    [ObservableProperty]
    public partial string FolderPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecentText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanResetFolder { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearRecentCommand))]
    public partial bool CanClearRecent { get; private set; }

    [ObservableProperty]
    public partial bool IsStartupOn { get; set; }

    [ObservableProperty]
    public partial string StartupDescription { get; private set; } = string.Empty;

    protected override void OnActivated()
    {
        RefreshFolder();
        _ = LoadStartupCommand.ExecuteAsync(null);
    }

    // Only a change away from the known state is the user's; applying that state lands here too.
    partial void OnIsStartupOnChanged(bool value)
    {
        if (value != IsOn(_startupState) && CanChangeStartup)
        {
            _ = SetStartupCommand.ExecuteAsync(value);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LoadStartupAsync() => RunStartupAsync("Read startup state", _startup.GetStateAsync);

    [RelayCommand]
    private Task SetStartupAsync(bool enabled) =>
        RunStartupAsync("Change startup state", token => _startup.SetEnabledAsync(enabled, token));

    [RelayCommand]
    private Task ChooseFolderAsync() => RunAsync("Choose folder", async token =>
    {
        string? path = await _navigation.PickFolderAsync(_folders.RootPath, token);
        token.ThrowIfCancellationRequested();
        if (path is not null)
        {
            _folders.SetRoot(path);
            RefreshFolder();
        }
    });

    [RelayCommand]
    private void ResetFolder() => Run("Reset folder", () =>
    {
        _folders.SetRoot(_folders.DownloadsPath);
        RefreshFolder();
    });

    [RelayCommand(CanExecute = nameof(CanClearRecent))]
    private void ClearRecent() => Run("Clear recent folders", () =>
    {
        _folders.ClearRecent();
        RefreshFolder();
    });

    [RelayCommand]
    private void Back() => _navigation.ShowMainPage();

    private static bool IsOn(StartupState state) => state is StartupState.Enabled or StartupState.EnabledByPolicy;

    private async Task RunStartupAsync(string operation, Func<CancellationToken, Task<StartupState>> request)
    {
        if (!IsVisible)
        {
            return;
        }

        using LatestRequest.Scope scope = _startupRequest.Begin(VisibleToken);
        CancellationToken token = scope.Token;
        try
        {
            StartupState state = await request(token);
            token.ThrowIfCancellationRequested();
            ApplyStartupState(state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportFailure(operation, ex, token);
            if (!token.IsCancellationRequested)
            {
                ApplyStartupState(_startupState);
            }
        }
    }

    private void StartupCommand_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(CanChangeStartup));
        }
    }

    private void RefreshFolder()
    {
        FolderPath = _folders.RootPath;
        CanResetFolder = !FolderPaths.Same(FolderPath, _folders.DownloadsPath);
        int count = _folders.RecentFolders.Count;
        CanClearRecent = count > 0;
        RecentText = Text.Format(count == 0 ? "NoRecentFolders" : count == 1 ? "RecentFolder" : "RecentFolders", count);
    }

    private void ApplyStartupState(StartupState state)
    {
        _startupState = state;
        IsStartupOn = IsOn(state);
        StartupDescription = Text.Get(state switch
        {
            StartupState.DisabledByUser => "StartupDisabledByUser",
            StartupState.DisabledByPolicy or StartupState.EnabledByPolicy => "StartupManaged",
            StartupState.Unavailable => "StartupUnavailable",
            _ => "StartupReady",
        });
        OnPropertyChanged(nameof(CanChangeStartup));
    }
}
