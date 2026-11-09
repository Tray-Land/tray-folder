using System.Globalization;

namespace TrayFolder.Core;

public sealed record FolderChoice(string Name, string Path, string Glyph)
{
    /// <summary>Segoe Fluent Icons folder, for any folder without a glyph of its own.</summary>
    public const string DefaultGlyph = "";
}

public interface IFolderRepository
{
    string RootPath { get; }
    string DownloadsPath { get; }
    IReadOnlyList<string> RecentFolders { get; }
    FolderViewOptions ViewOptions { get; set; }
    event EventHandler? RootChanged;
    void SetRoot(string path);
    void ClearRecent();
    bool DirectoryExists(string path);
    Task<IReadOnlyList<FileEntry>> EnumerateAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<FolderChoice>> GetFolderChoicesAsync(CancellationToken cancellationToken);
}

public interface IFileActions
{
    Task OpenAsync(string path, CancellationToken cancellationToken);
    Task OpenWithAsync(string path, CancellationToken cancellationToken);
    Task ShowInExplorerAsync(string path, CancellationToken cancellationToken);
    Task OpenFolderAsync(string path, CancellationToken cancellationToken);
    Task CopyAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken);

    /// <summary>Puts the files on the clipboard to be moved: pasting in Explorer moves them out of this folder.</summary>
    Task CutAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken);

    Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken);
}

/// <summary>An item sent to the Recycle Bin; <paramref name="RecordPath"/> is its <c>$I</c> record there, which restoring needs.</summary>
public sealed record DeletedItem(FileEntry Entry, string RecordPath);

/// <summary>What a delete did: <paramref name="Recycled"/> can be undone, <paramref name="Gone"/> went without a bin record, <paramref name="Remaining"/> is still on disk.</summary>
public sealed record DeleteOutcome(IReadOnlyList<DeletedItem> Recycled, int Gone, int Remaining);

public interface IFileDeleter
{
    /// <summary>Sends the entries to the Recycle Bin the way Explorer does, with the shell's own prompts and error dialogs.</summary>
    Task<DeleteOutcome> DeleteAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken);

    /// <summary>Puts recycled items back where they were; returns how many came back.</summary>
    Task<int> RestoreAsync(IReadOnlyList<DeletedItem> items, CancellationToken cancellationToken);
}

public enum StartupState
{
    Unavailable,
    Disabled,
    Enabled,
    DisabledByUser,
    DisabledByPolicy,
    EnabledByPolicy,
}

public interface IStartupService
{
    Task<StartupState> GetStateAsync(CancellationToken cancellationToken);
    Task<StartupState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

public interface ITextProvider
{
    string Get(string key);

    string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}

public interface IErrorReporter
{
    void Report(string operation, Exception exception);
}

/// <summary>Watches one folder. <see cref="Changed"/> fires on the UI thread once a burst of changes settles.</summary>
public interface IFolderWatcher
{
    event EventHandler? Changed;
    void Watch(string path);
    void Stop();
}

/// <summary>
/// Platform objects a row holds on to. They are opaque here so ViewModels stay free of WinRT types
/// and testable; XAML casts them back in its bindings.
/// </summary>
public interface IFileResources
{
    /// <summary>The IStorageItem a drag hands over. Drag events can't await, so rows resolve it ahead of time.</summary>
    Task<object> ResolveAsync(FileEntry entry, CancellationToken cancellationToken);

    /// <summary>An ImageSource of the file's shell thumbnail, <paramref name="size"/> in layout pixels; null if the shell has none.</summary>
    Task<object?> LoadThumbnailAsync(string path, uint size, CancellationToken cancellationToken);
}

/// <summary>
/// What a file's preview shows. Which content property is set says how to show it; none set means
/// there is no preview. Image, Media and Thumbnail are opaque platform objects (see <see cref="IFileResources"/>).
/// </summary>
public sealed record PreviewResult(
    string TypeName,
    string? Dimensions = null,
    uint PageCount = 0,
    object? Image = null,
    string? Text = null,
    object? Media = null,
    object? Thumbnail = null)
{
    public static PreviewResult Empty { get; } = new(string.Empty);

    public bool HasImage => Image is not null;

    public bool HasText => Text is not null;

    public bool HasMedia => Media is not null;

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasContent => HasImage || HasText || HasMedia;
}

public sealed record FileInformation(string Label, string Value);

/// <summary>Produces preview content; the ViewModel owns which file is shown, its requests, and its state.</summary>
public interface IFilePreview
{
    Task<PreviewResult> LoadAsync(FileEntry entry, CancellationToken cancellationToken);

    /// <summary>An ImageSource of one page of the PDF that <see cref="LoadAsync"/> opened.</summary>
    Task<object> RenderPageAsync(uint pageIndex, CancellationToken cancellationToken);

    /// <summary>Releases the open document and media.</summary>
    void Clear();
}
