namespace TrayFolder.Core;

public enum SortField
{
    Name,
    DateModified,
    Type,
    Size,
}

public enum GroupField
{
    None,
    Name,
    DateModified,
    Kind,
    Size,
}

public enum DateFilter
{
    Any,
    Today,
    LastWeek,
    LastMonth,
    LastYear,
}

public enum ViewMode
{
    List,
    Tiles,
}

/// <summary>A file or folder as enumerated from disk. No platform dependencies.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsFolder,
    long Size,
    DateTime Modified,
    DateTime Created,
    bool IsHidden)
{
    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    public FileKind Kind => FileKinds.KindOf(Extension, IsFolder);
}

/// <summary>
/// The Explorer-style view settings the user picks in the flyout: sort, group, filter and layout.
/// Mutable so the options menus can flip one field and persist the whole thing as JSON.
/// </summary>
public sealed class FolderViewOptions
{
    // Defaults suit the main use: the newest download first, grouped the way Explorer groups Downloads.
    public SortField SortBy { get; set; } = SortField.DateModified;

    public bool Descending { get; set; } = true;

    public GroupField GroupBy { get; set; } = GroupField.DateModified;

    public ViewMode View { get; set; } = ViewMode.List;

    public DateFilter Date { get; set; } = DateFilter.Any;

    /// <summary>Kinds to show; empty means every kind.</summary>
    public List<FileKind> Kinds { get; set; } = [];

    public bool ShowFolders { get; set; } = true;

    public bool ShowHidden { get; set; }

    public bool ShowExtensions { get; set; } = true;

    public bool IsFiltered => Kinds.Count > 0 || Date != DateFilter.Any || !ShowFolders;
}
