namespace TrayFolder.ViewModels;

public interface IFlyoutNavigation
{
    void ShowMainPage();
    void ShowSettingsPage();
    void ShowDetailPage(FileItemViewModel item, IReadOnlyList<FileItemViewModel> siblings);
    Task<string?> PickFolderAsync(string startFolder, CancellationToken cancellationToken);
}
