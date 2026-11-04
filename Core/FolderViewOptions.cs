using CommunityToolkit.Mvvm.ComponentModel;

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
/// Observable so the options menus bind straight to it; persisted whole as JSON.
/// </summary>
public sealed partial class FolderViewOptions : ObservableObject
{
    // Defaults suit the main use: the newest download first, grouped the way Explorer groups Downloads.
    [ObservableProperty]
    public partial SortField SortBy { get; set; } = SortField.DateModified;

    [ObservableProperty]
    public partial bool Descending { get; set; } = true;

    [ObservableProperty]
    public partial GroupField GroupBy { get; set; } = GroupField.DateModified;

    [ObservableProperty]
    public partial ViewMode View { get; set; } = ViewMode.List;

    [ObservableProperty]
    public partial DateFilter Date { get; set; } = DateFilter.Any;

    /// <summary>Kinds to show; empty means every kind. Replace the list to change it, so bindings hear about it.</summary>
    [ObservableProperty]
    public partial List<FileKind> Kinds { get; set; } = [];

    [ObservableProperty]
    public partial bool ShowFolders { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial bool ShowExtensions { get; set; } = true;

    public bool IsFiltered => Kinds.Count > 0 || Date != DateFilter.Any || !ShowFolders;
}
