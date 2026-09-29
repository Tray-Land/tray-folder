using System.Runtime.InteropServices;
using TrayFolder.Core;

namespace TrayFolder.Services;

/// <summary>Reads folders from disk and tracks which folder the flyout shows.</summary>
internal static partial class FolderService
{
    private const int MaxRecentFolders = 6;

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary>Raised when the chosen folder changes, from the flyout or its settings page.</summary>
    public static event EventHandler? RootChanged;

    public static string DownloadsPath => GetKnownFolder(FolderIdDownloads)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>The folder the flyout opens on: the user's pick, or Downloads.</summary>
    public static string RootPath => SettingsService.FolderPath is { Length: > 0 } path ? path : DownloadsPath;

    /// <summary>The standard folders offered in the folder menu, skipping any that don't exist.</summary>
    public static IEnumerable<(string Name, string Path, string Glyph)> KnownFolders()
    {
        (string, string?, string)[] folders =
        [
            ("Downloads", DownloadsPath, ""),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ""),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ""),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ""),
            ("Screenshots", GetKnownFolder(FolderIdScreenshots), ""),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ""),
        ];

        foreach ((string name, string? path, string glyph) in folders)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                yield return (name, path, glyph);
            }
        }
    }

    public static void SetRoot(string path)
    {
        SettingsService.FolderPath = path;

        // Known folders always have their own menu entries; only other picks go in the recent list.
        List<string> recent = SettingsService.RecentFolders;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (!KnownFolders().Any(k => string.Equals(Path.TrimEndingDirectorySeparator(k.Path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)))
        {
            recent.Insert(0, path);
        }

        SettingsService.RecentFolders = recent.Take(MaxRecentFolders).ToList();

        RootChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearRecent() => SettingsService.RecentFolders = [];

    /// <summary>Display name for a folder path: the known-folder name, or the last segment.</summary>
    public static string DisplayName(string path)
    {
        foreach ((string name, string knownPath, _) in KnownFolders())
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(knownPath), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name2 = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name2) ? trimmed : name2;
    }

    /// <summary>Lists a folder's direct children. Runs on a worker thread; skips protected OS files as Explorer does.</summary>
    public static Task<List<FileEntry>> EnumerateAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        List<FileEntry> entries = [];
        foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = info.Attributes;

            // desktop.ini and friends: hidden *and* system stays hidden even with "Hidden items" on.
            if (attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System))
            {
                continue;
            }

            bool isFolder = attributes.HasFlag(FileAttributes.Directory);
            entries.Add(new FileEntry(
                info.Name,
                info.FullName,
                isFolder,
                isFolder ? 0 : ((FileInfo)info).Length,
                info.LastWriteTime,
                info.CreationTime,
                attributes.HasFlag(FileAttributes.Hidden)));
        }

        return entries;
    }, cancellationToken);

    private static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, 0, out nint pathPtr) != 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
