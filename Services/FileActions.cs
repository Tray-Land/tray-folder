using System.Diagnostics;
using TrayFolder.Core;
using TrayFolder.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TrayFolder.Services;

/// <summary>Files as the shell sees them: storage items, thumbnails, the clipboard, and launching.</summary>
/// <param name="rasterizationScale">Physical pixels per layout pixel, for thumbnail sizes. Read on the UI thread.</param>
internal sealed class FileActions(Func<double> rasterizationScale) : IFileActions, IFileResources
{
    /// <summary>Resolves on a worker thread: on the UI thread this can pump messages inside a layout callback, which XAML fails fast on.</summary>
    public static Task<IStorageItem> ResolveStorageItemAsync(FileEntry entry, CancellationToken cancellationToken) =>
        Task.Run(async () => entry.IsFolder
            ? (IStorageItem)await StorageFolder.GetFolderFromPathAsync(entry.FullPath).AsTask(cancellationToken)
            : await StorageFile.GetFileFromPathAsync(entry.FullPath).AsTask(cancellationToken), cancellationToken);

    public static void FillDragData(DataPackage data, IReadOnlyList<FileItemViewModel> items, string title)
    {
        data.RequestedOperation = DataPackageOperation.Copy;
        data.Properties.Title = title;
        if (items.All(i => i.StorageItem is not null))
        {
            data.SetStorageItems(items.Select(i => (IStorageItem)i.StorageItem!), readOnly: false);
            return;
        }

        FileEntry[] entries = [.. items.Select(i => i.Entry)];
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            DataProviderDeferral deferral = request.GetDeferral();
            try
            {
                // This is the drop target's one-shot transfer, not a task owned by the hidden view.
                IStorageItem[] resolved = await Task.WhenAll(entries.Select(e => ResolveStorageItemAsync(e, CancellationToken.None)));
                request.SetData(resolved);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Resolve drag data failed: {ex}");
                throw;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    public async Task<object> ResolveAsync(FileEntry entry, CancellationToken cancellationToken) =>
        await ResolveStorageItemAsync(entry, cancellationToken);

    public async Task<object?> LoadThumbnailAsync(string path, uint size, CancellationToken cancellationToken)
    {
        int pixelSize = (int)Math.Ceiling(size * rasterizationScale());
        ShellThumbnail.Pixels? pixels = await Task.Run(() => ShellThumbnail.Load(path, pixelSize), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return pixels is null ? null : ShellThumbnail.ToImage(pixels);
    }

    public async Task CopyAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        IStorageItem[] resolved = await Task.WhenAll(entries.Select(e => ResolveStorageItemAsync(e, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        if (resolved.Length == 0)
        {
            throw new InvalidOperationException("No files were selected for copying.");
        }

        DataPackage data = new() { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems(resolved, readOnly: false);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    public Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataPackage data = new();
        data.SetText(string.Join(Environment.NewLine, entries.Select(i => $"\"{i.FullPath}\"")));
        Clipboard.SetContent(data);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    public Task OpenAsync(string path, CancellationToken cancellationToken) => ShellAsync(path, null, cancellationToken);

    public Task OpenWithAsync(string path, CancellationToken cancellationToken) =>
        ShellAsync("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}", cancellationToken);

    public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) =>
        ShellAsync("explorer.exe", $"/select,\"{path}\"", cancellationToken);

    public Task OpenFolderAsync(string path, CancellationToken cancellationToken) =>
        ShellAsync("explorer.exe", $"\"{path}\"", cancellationToken);

    private static Task ShellAsync(string file, string? arguments, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process? process = Process.Start(new ProcessStartInfo(file)
        {
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
        });
    }, cancellationToken);
}
