namespace TrayFolder.ViewModels;

/// <summary>A labeled run of rows in the grouped list. Views/FileGroupViewModel.Bindable.cs makes it bindable.</summary>
public sealed partial class FileGroupViewModel(string header, List<FileItemViewModel> items, string countText)
{
    public string Header { get; } = header;

    public List<FileItemViewModel> Items { get; } = items;

    public string CountText { get; } = countText;

    // Screen readers announce the group by this.
    public override string ToString() => Header.Length == 0 ? CountText : $"{Header}, {CountText}";
}
