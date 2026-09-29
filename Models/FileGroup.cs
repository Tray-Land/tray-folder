namespace TrayFolder.Models;

/// <summary>
/// A group for the grouped list (CollectionViewSource.ItemsPath = Items). The group header uses
/// {Binding}, so the type is [Bindable] to keep it working when trimmed.
/// </summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class FileGroup(string header, IEnumerable<FileItem> items)
{
    public string Header { get; } = header;

    public List<FileItem> Items { get; } = items.ToList();

    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    // Screen readers announce the group by this.
    public override string ToString() => string.IsNullOrEmpty(Header) ? CountText : $"{Header}, {CountText}";
}
