using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using TrayFolder.Core;
using TrayFolder.Services;
using Windows.Storage;

namespace TrayFolder.Models;

/// <summary>
/// One row in the flyout. Wraps the disk entry, and lazily resolves the shell's StorageItem (for
/// dragging and the clipboard) and its thumbnail, both only for rows that get realized on screen.
/// </summary>
public sealed partial class FileItem : INotifyPropertyChanged
{
    private Task<IStorageItem?>? _storageTask;
    private uint _thumbnailSize;
    private bool _showExtension = true;

    public FileItem(FileEntry entry)
    {
        Entry = entry;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry Entry { get; }

    public string FullPath => Entry.FullPath;

    public bool IsFolder => Entry.IsFolder;

    public string Glyph => FileKinds.Glyph(Entry.Kind);

    public double ItemOpacity => Entry.IsHidden ? 0.6 : 1.0;

    public string DisplayName => _showExtension || IsFolder ? Entry.Name : Path.GetFileNameWithoutExtension(Entry.Name);

    public string SizeText => IsFolder ? string.Empty : FolderViewPolicy.FormatSize(Entry.Size);

    public string ModifiedText => FormatDate(Entry.Modified);

    /// <summary>Second line in the list: "Today 9:41 AM · 2.3 MB".</summary>
    public string Details => IsFolder ? $"{ModifiedText} · Folder" : $"{ModifiedText} · {SizeText}";

    /// <summary>Resolved once the shell has been asked; null until then (or if it couldn't be read).</summary>
    public IStorageItem? StorageItem { get; private set; }

    public ImageSource? Thumbnail { get; private set; }

    /// <summary>The kind glyph is a placeholder; it would show through a transparent thumbnail.</summary>
    public Microsoft.UI.Xaml.Visibility GlyphVisibility => Thumbnail is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The shell's type name ("PNG File"), once the StorageItem is resolved.</summary>
    public string TypeName => StorageItem switch
    {
        StorageFile file when !string.IsNullOrEmpty(file.DisplayType) => file.DisplayType,
        StorageFolder => "File folder",
        _ => IsFolder ? "File folder" : $"{Entry.Extension.TrimStart('.').ToUpperInvariant()} File",
    };

    public bool ShowExtension
    {
        get => _showExtension;
        set
        {
            if (_showExtension != value)
            {
                _showExtension = value;
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public static string FormatDate(DateTime date)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTime today = DateTime.Today;
        string time = date.ToString("t", culture);
        return date.Date == today ? $"Today {time}"
            : date.Date == today.AddDays(-1) ? $"Yesterday {time}"
            : date.Year == today.Year ? $"{date.ToString("MMM d", culture)} {time}"
            : date.ToString("d", culture);
    }

    /// <summary>Resolves the StorageFile/StorageFolder once; the task is shared by thumbnails, drag and copy.</summary>
    public Task<IStorageItem?> GetStorageItemAsync() => _storageTask ??= ResolveStorageItemAsync();

    /// <summary>Loads the shell thumbnail at <paramref name="size"/> px unless one at least that big is loaded.</summary>
    public async Task LoadThumbnailAsync(uint size)
    {
        if (_thumbnailSize >= size)
        {
            return;
        }

        _thumbnailSize = size;

        // Rows on screen are the likeliest to be dragged; have the StorageItem ready for it.
        _ = GetStorageItemAsync();

        // Shell calls run on a worker: made on the UI thread they can pump messages mid-layout,
        // which XAML treats as reentrancy and fails fast.
        string path = FullPath;
        ShellThumbnail.Pixels? pixels = await Task.Run(() => ShellThumbnail.Load(path, (int)size));
        if (pixels is null)
        {
            // No thumbnail (offline cloud file, locked file): the kind glyph stays.
            _thumbnailSize = 0;
            return;
        }

        Thumbnail = ShellThumbnail.ToImage(pixels);
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(GlyphVisibility));
    }

    private async Task<IStorageItem?> ResolveStorageItemAsync()
    {
        try
        {
            string path = FullPath;
            bool isFolder = IsFolder;
            StorageItem = await Task.Run(async () => isFolder
                ? (IStorageItem)await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path));
            OnPropertyChanged(nameof(TypeName));
            return StorageItem;
        }
        catch
        {
            // Deleted or inaccessible since the folder was read; let the next refresh drop it.
            _storageTask = null;
            return null;
        }
    }

    // List items take their accessible name from this.
    public override string ToString() => $"{Entry.Name}, {Details}";

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
