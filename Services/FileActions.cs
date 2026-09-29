using System.Diagnostics;
using TrayFolder.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TrayFolder.Services;

/// <summary>What the user can do with files: drag them out, copy them, open them, find them.</summary>
internal static class FileActions
{
    /// <summary>
    /// Fills a drag's DataPackage with the files. Drag events are synchronous, so rows whose
    /// StorageItem is already resolved (the pointer-press prefetch nearly always gets there first)
    /// go in directly; otherwise the package promises the items and resolves them on drop.
    /// </summary>
    public static void FillDragData(DataPackage data, IReadOnlyList<FileItem> items)
    {
        // Copy, never move: a drop on a folder on the same drive would otherwise take the file
        // out of the watched folder.
        data.RequestedOperation = DataPackageOperation.Copy;
        data.Properties.Title = items.Count == 1 ? items[0].Entry.Name : $"{items.Count} items";

        if (items.All(i => i.StorageItem is not null))
        {
            data.SetStorageItems(items.Select(i => i.StorageItem!), readOnly: false);
            return;
        }

        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            DataProviderDeferral deferral = request.GetDeferral();
            try
            {
                request.SetData(await ResolveAsync(items));
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>Puts the files on the clipboard, like Ctrl+C in Explorer, so they paste into mail or a folder.</summary>
    public static async Task<bool> CopyToClipboardAsync(IReadOnlyList<FileItem> items)
    {
        List<IStorageItem> storage = await ResolveAsync(items);
        if (storage.Count == 0)
        {
            return false;
        }

        DataPackage data = new() { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems(storage, readOnly: false);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        return true;
    }

    /// <summary>Explorer's "Copy as path": quoted full paths, one per line.</summary>
    public static void CopyPaths(IEnumerable<FileItem> items)
    {
        DataPackage data = new();
        data.SetText(string.Join(Environment.NewLine, items.Select(i => $"\"{i.FullPath}\"")));
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    public static bool Open(string path) => Shell(path, null);

    public static bool OpenWith(string path) => Shell("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");

    public static bool ShowInExplorer(string path) => Shell("explorer.exe", $"/select,\"{path}\"");

    public static bool OpenFolder(string path) => Shell("explorer.exe", $"\"{path}\"");

    private static async Task<List<IStorageItem>> ResolveAsync(IEnumerable<FileItem> items)
    {
        IStorageItem?[] resolved = await Task.WhenAll(items.Select(i => i.GetStorageItemAsync()));
        return resolved.OfType<IStorageItem>().ToList();
    }

    private static bool Shell(string file, string? arguments)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(file)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
