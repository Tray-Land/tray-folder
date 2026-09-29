using System.Globalization;
using TrayFolder.Core;

namespace TrayFolder.Tests;

[TestClass]
public sealed class FolderViewPolicyTests
{
    // A Wednesday, so "earlier this week" and "last week" are both reachable.
    private static readonly DateTime Now = new(2026, 9, 30, 15, 0, 0);

    private static FileEntry File(string name, DateTime modified, long size = 1000, bool hidden = false) =>
        new(name, $@"C:\f\{name}", false, size, modified, modified, hidden);

    private static FileEntry Folder(string name, DateTime modified) =>
        new(name, $@"C:\f\{name}", true, 0, modified, modified, false);

    [TestMethod]
    public void DefaultOptions_NewestFirst_GroupedByDate()
    {
        FileEntry[] entries =
        [
            File("old.pdf", Now.AddYears(-2)),
            File("today.png", Now.AddHours(-1)),
            File("yesterday.zip", Now.AddDays(-1)),
            File("today-earlier.png", Now.AddHours(-5)),
        ];

        IReadOnlyList<EntryGroup> groups = FolderViewPolicy.Arrange(entries, new FolderViewOptions(), null, Now, DayOfWeek.Sunday);

        CollectionAssert.AreEqual(new[] { "Today", "Yesterday", "A long time ago" }, groups.Select(g => g.Header).ToArray());
        CollectionAssert.AreEqual(new[] { "today.png", "today-earlier.png" }, groups[0].Items.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void OldestFirst_ReversesDateGroups()
    {
        FileEntry[] entries = [File("a", Now), File("b", Now.AddYears(-3))];
        FolderViewOptions options = new() { Descending = false };

        IReadOnlyList<EntryGroup> groups = FolderViewPolicy.Arrange(entries, options, null, Now, DayOfWeek.Sunday);

        Assert.AreEqual("A long time ago", groups[0].Header);
    }

    [TestMethod]
    public void DateGroups_FollowTheCalendar()
    {
        Assert.AreEqual("Earlier this week", FolderViewPolicy.DateGroup(new DateTime(2026, 9, 27, 9, 0, 0), Now, DayOfWeek.Sunday).Header);
        Assert.AreEqual("Last week", FolderViewPolicy.DateGroup(new DateTime(2026, 9, 21), Now, DayOfWeek.Sunday).Header);
        Assert.AreEqual("Earlier this month", FolderViewPolicy.DateGroup(new DateTime(2026, 9, 5), Now, DayOfWeek.Sunday).Header);
        Assert.AreEqual("Last month", FolderViewPolicy.DateGroup(new DateTime(2026, 8, 5), Now, DayOfWeek.Sunday).Header);
        Assert.AreEqual("Earlier this year", FolderViewPolicy.DateGroup(new DateTime(2026, 2, 5), Now, DayOfWeek.Sunday).Header);

        // Monday-first cultures: Sunday the 27th belongs to last week.
        Assert.AreEqual("Last week", FolderViewPolicy.DateGroup(new DateTime(2026, 9, 27, 9, 0, 0), Now, DayOfWeek.Monday).Header);
    }

    [TestMethod]
    public void FoldersFirst_ThenNaturalNameOrder()
    {
        FileEntry[] entries = [File("file10.txt", Now), File("file2.txt", Now), Folder("zeta", Now), File("File1.txt", Now)];
        FolderViewOptions options = new() { SortBy = SortField.Name, Descending = false, GroupBy = GroupField.None };

        IReadOnlyList<EntryGroup> groups = FolderViewPolicy.Arrange(entries, options, null, Now, DayOfWeek.Sunday);

        CollectionAssert.AreEqual(
            new[] { "zeta", "File1.txt", "file2.txt", "file10.txt" },
            groups.Single().Items.Select(e => e.Name).ToArray());
        Assert.AreEqual(string.Empty, groups.Single().Header);
    }

    [TestMethod]
    public void SortBySize_Descending_LargestFirst()
    {
        FileEntry[] entries = [File("s", Now, 10), File("l", Now, 10_000_000), File("m", Now, 50_000)];
        FolderViewOptions options = new() { SortBy = SortField.Size, Descending = true, GroupBy = GroupField.Size };

        IReadOnlyList<EntryGroup> groups = FolderViewPolicy.Arrange(entries, options, null, Now, DayOfWeek.Sunday);

        CollectionAssert.AreEqual(
            new[] { "Medium (1 - 128 MB)", "Small (16 KB - 1 MB)", "Tiny (0 - 16 KB)" },
            groups.Select(g => g.Header).ToArray());
    }

    [TestMethod]
    public void KindFilter_KeepsFolders_AndSearchMatchesName()
    {
        FileEntry[] entries = [File("photo.JPG", Now), File("notes.txt", Now), Folder("Photos", Now), File("pic.png", Now)];
        FolderViewOptions options = new() { Kinds = [FileKind.Image], GroupBy = GroupField.None };

        List<string> images = FolderViewPolicy.Arrange(entries, options, null, Now, DayOfWeek.Sunday).Single().Items.Select(e => e.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "photo.JPG", "Photos", "pic.png" }, images);

        List<string> searched = FolderViewPolicy.Arrange(entries, options, "PHOTO", Now, DayOfWeek.Sunday).Single().Items.Select(e => e.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "photo.JPG", "Photos" }, searched);
    }

    [TestMethod]
    public void DateFilter_And_HiddenFiles()
    {
        FileEntry[] entries = [File("new", Now), File("old", Now.AddDays(-10)), File("secret", Now, hidden: true)];

        FolderViewOptions pastWeek = new() { Date = DateFilter.LastWeek, GroupBy = GroupField.None };
        CollectionAssert.AreEqual(new[] { "new" }, FolderViewPolicy.Arrange(entries, pastWeek, null, Now, DayOfWeek.Sunday).Single().Items.Select(e => e.Name).ToArray());

        FolderViewOptions showHidden = new() { ShowHidden = true, GroupBy = GroupField.None };
        Assert.AreEqual(3, FolderViewPolicy.Arrange(entries, showHidden, null, Now, DayOfWeek.Sunday).Single().Items.Count);
    }

    [TestMethod]
    public void NothingVisible_ReturnsNoGroups()
    {
        FolderViewOptions options = new() { GroupBy = GroupField.None };
        Assert.AreEqual(0, FolderViewPolicy.Arrange([File("a", Now)], options, "zzz", Now, DayOfWeek.Sunday).Count);
    }

    [TestMethod]
    public void NameGroups_UseExplorerRanges()
    {
        Assert.AreEqual("A - H", FolderViewPolicy.NameGroup("banana").Header);
        Assert.AreEqual("I - P", FolderViewPolicy.NameGroup("Invoice.pdf").Header);
        Assert.AreEqual("Q - Z", FolderViewPolicy.NameGroup("zip").Header);
        Assert.AreEqual("0 - 9", FolderViewPolicy.NameGroup("2026 taxes").Header);
        Assert.AreEqual("Other", FolderViewPolicy.NameGroup("_draft").Header);
    }

    [TestMethod]
    public void FormatSize_LikeExplorer()
    {
        CultureInfo en = CultureInfo.GetCultureInfo("en-US");
        Assert.AreEqual("512 bytes", FolderViewPolicy.FormatSize(512, en));
        Assert.AreEqual("1.5 KB", FolderViewPolicy.FormatSize(1536, en));
        Assert.AreEqual("2.4 MB", FolderViewPolicy.FormatSize(2_500_000, en));
    }

    [TestMethod]
    public void Previews_ByExtension()
    {
        Assert.AreEqual(PreviewKind.Image, FileKinds.PreviewOf(".PNG", 1000));
        Assert.AreEqual(PreviewKind.None, FileKinds.PreviewOf(".png", FileKinds.MaxImagePreviewBytes + 1));
        Assert.AreEqual(PreviewKind.Svg, FileKinds.PreviewOf(".svg", 1000));
        Assert.AreEqual(PreviewKind.Pdf, FileKinds.PreviewOf(".pdf", 1000));
        Assert.AreEqual(PreviewKind.Text, FileKinds.PreviewOf(".json", 1000));
        Assert.AreEqual(PreviewKind.Media, FileKinds.PreviewOf(".mp4", 1000));
        Assert.AreEqual(PreviewKind.None, FileKinds.PreviewOf(".docx", 1000));
        Assert.AreEqual(FileKind.Archive, FileKinds.KindOf(".zip"));
        Assert.AreEqual(FileKind.Folder, FileKinds.KindOf(string.Empty, isFolder: true));
    }
}
