namespace TrayFolder.ViewModels;

/// <summary>A yes/no question the flyout asks before doing something it can't take back without effort.</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText, string CancelText);

public interface IFlyoutNavigation
{
    void ShowMainPage();
    void ShowSettingsPage();
    void ShowDetailPage(FileItemViewModel item, IReadOnlyList<FileItemViewModel> siblings);
    Task<string?> PickFolderAsync(string startFolder, CancellationToken cancellationToken);

    /// <summary>Asks inside the flyout. The default answer is Cancel; hiding the flyout answers no.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken cancellationToken);

    /// <summary>Keeps the flyout from light-dismissing (for example behind a shell dialog) until disposed.</summary>
    IDisposable KeepOpen();
}
