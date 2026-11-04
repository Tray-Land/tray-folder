using Microsoft.Extensions.Time.Testing;
using System.Text;
using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

[TestClass]
public sealed class DeletePolicyTests
{
    [TestMethod]
    public void Assess_OneOrdinaryFile_NeedsNoConfirmation()
    {
        DeleteAssessment result = DeletePolicy.Assess([TestEntries.File("notes.txt")]);

        Assert.IsFalse(result.RequiresConfirmation);
    }

    [TestMethod]
    public void Assess_LargeCount_FlagsManyItems()
    {
        FileEntry[] files = [.. Enumerable.Range(0, DeletePolicy.LargeCount).Select(i => TestEntries.File($"{i}.txt"))];

        Assert.IsTrue(DeletePolicy.Assess(files).Concerns.HasFlag(DeleteConcern.ManyItems));
        Assert.IsFalse(DeletePolicy.Assess(files[1..]).Concerns.HasFlag(DeleteConcern.ManyItems));
    }

    [TestMethod]
    public void Assess_Folder_FlagsFolders()
    {
        DeleteAssessment result = DeletePolicy.Assess([TestEntries.Folder("photos")]);

        Assert.AreEqual(DeleteConcern.Folders, result.Concerns);
        Assert.AreEqual(1, result.Folders);
    }

    [TestMethod]
    public void Assess_ProgramButNotShortcut_FlagsPrograms()
    {
        Assert.IsTrue(DeletePolicy.Assess([TestEntries.File("setup.exe")]).Concerns.HasFlag(DeleteConcern.Programs));
        Assert.IsTrue(DeletePolicy.Assess([TestEntries.File("run.ps1")]).Concerns.HasFlag(DeleteConcern.Programs));
        Assert.IsFalse(DeletePolicy.Assess([TestEntries.File("Docs.lnk")]).RequiresConfirmation);
    }

    [TestMethod]
    public void Assess_SystemAndReadOnlyFiles_AreFlagged()
    {
        DeleteAssessment result = DeletePolicy.Assess([
            TestEntries.File("a.dat") with { IsSystem = true },
            TestEntries.File("b.dat") with { IsReadOnly = true }]);

        Assert.AreEqual(DeleteConcern.SystemFiles | DeleteConcern.ReadOnlyFiles, result.Concerns);
    }

    [TestMethod]
    public void Assess_LargeTotalSize_FlagsLargeSize()
    {
        DeleteAssessment result = DeletePolicy.Assess([TestEntries.File("movie.dat") with { Size = DeletePolicy.LargeBytes }]);

        Assert.AreEqual(DeleteConcern.LargeSize, result.Concerns);
    }
}

[TestClass]
public sealed class RecycleRecordTests
{
    private static byte[] Version2(string path, long size = 42, long fileTime = 133_000_000_000_000_000)
    {
        byte[] text = Encoding.Unicode.GetBytes(path + '\0');
        return [.. BitConverter.GetBytes(2L), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(fileTime),
            .. BitConverter.GetBytes(path.Length + 1), .. text];
    }

    [TestMethod]
    public void Parse_Version2_ReadsPathSizeAndTime()
    {
        RecycleRecord? record = RecycleRecord.Parse(Version2(@"C:\Users\me\Downloads\a b.txt", 99));

        Assert.IsNotNull(record);
        Assert.AreEqual(@"C:\Users\me\Downloads\a b.txt", record.OriginalPath);
        Assert.AreEqual(99, record.Size);
        Assert.AreEqual(DateTime.FromFileTimeUtc(133_000_000_000_000_000), record.DeletedUtc);
    }

    [TestMethod]
    public void Parse_Version1_ReadsFixedWidthPath()
    {
        byte[] path = new byte[520];
        Encoding.Unicode.GetBytes(@"D:\old.txt").CopyTo(path, 0);
        byte[] data = [.. BitConverter.GetBytes(1L), .. BitConverter.GetBytes(7L), .. BitConverter.GetBytes(133_000_000_000_000_000), .. path];

        Assert.AreEqual(@"D:\old.txt", RecycleRecord.Parse(data)?.OriginalPath);
    }

    [TestMethod]
    public void Parse_TruncatedOrUnknown_ReturnsNull()
    {
        byte[] truncated = Version2(@"C:\a.txt")[..40];
        byte[] unknown = [.. BitConverter.GetBytes(9L), .. new byte[40]];

        Assert.IsNull(RecycleRecord.Parse(truncated));
        Assert.IsNull(RecycleRecord.Parse(unknown));
        Assert.IsNull(RecycleRecord.Parse([]));
    }
}

[TestClass]
public sealed class FolderViewModelDeleteTests
{
    private static async Task<FolderViewModel> ShowAsync(
        TestFolders folders, TestDeleter deleter, TestNavigation navigation, FakeTimeProvider? time = null)
    {
        FolderViewModel vm = new(folders, new TestFileActions(), deleter, new TestResources(), new TestWatcher(), navigation,
            new TestTextProvider(), time ?? TestTime.Create(), new TestErrors());
        vm.OnShown();
        await vm.RefreshCommand.ExecutionTask!;
        return vm;
    }

    [TestMethod]
    public async Task DeleteTargets_OrdinaryFile_DeletesWithoutAskingAndOffersUndo()
    {
        TestDeleter deleter = new();
        TestNavigation navigation = new();
        using FolderViewModel vm = await ShowAsync(new TestFolders { Entries = [TestEntries.File("a.txt")] }, deleter, navigation);
        vm.MenuTarget = vm.Items[0];

        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        Assert.AreEqual(0, navigation.Confirmations.Count);
        Assert.AreEqual("a.txt", deleter.Deletes.Single().Single().Name);
        Assert.IsTrue(vm.IsDeleteNoticeOpen);
        Assert.IsTrue(vm.CanUndoDelete);
        StringAssert.Contains(vm.DeleteNoticeText, "a.txt");
        Assert.AreEqual(1, navigation.KeepOpenCount);
        Assert.AreEqual(1, navigation.KeepOpenReleased);
    }

    [TestMethod]
    public async Task DeleteTargets_MenuOpenedOnSelectedRow_DeletesWholeSelection()
    {
        TestDeleter deleter = new();
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt"), TestEntries.File("c.txt")] }, deleter, new TestNavigation());
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);
        vm.MenuTarget = vm.Items[0];

        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        Assert.AreEqual(2, deleter.Deletes.Single().Count);
        Assert.AreEqual("Deleted 2 items", vm.DeleteNoticeText);
    }

    [TestMethod]
    public async Task DeleteTargets_Folder_AsksFirstAndDoesNothingWhenDeclined()
    {
        TestDeleter deleter = new();
        TestNavigation navigation = new() { ConfirmAnswer = false };
        using FolderViewModel vm = await ShowAsync(new TestFolders { Entries = [TestEntries.Folder("photos")] }, deleter, navigation);
        vm.MenuTarget = vm.Items[0];

        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        Assert.AreEqual(1, navigation.Confirmations.Count);
        StringAssert.Contains(navigation.Confirmations[0].Title, "photos");
        Assert.AreEqual(0, deleter.Deletes.Count);
        Assert.IsFalse(vm.IsDeleteNoticeOpen);
    }

    [TestMethod]
    public async Task DeleteTargets_ManyFiles_ConfirmedThenDeleted()
    {
        TestDeleter deleter = new();
        TestNavigation navigation = new();
        FileEntry[] files = [.. Enumerable.Range(0, DeletePolicy.LargeCount).Select(i => TestEntries.File($"{i}.txt"))];
        using FolderViewModel vm = await ShowAsync(new TestFolders { Entries = files }, deleter, navigation);
        foreach (FileItemViewModel item in vm.Items)
        {
            vm.SelectedItems.Add(item);
        }

        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        Assert.AreEqual(1, navigation.Confirmations.Count);
        Assert.AreEqual(DeletePolicy.LargeCount, deleter.Deletes.Single().Count);
    }

    [TestMethod]
    public async Task DeleteTargets_NoBinRecord_ShowsNoticeWithoutUndo()
    {
        TestDeleter deleter = new() { Recyclable = 0 };
        using FolderViewModel vm = await ShowAsync(new TestFolders { Entries = [TestEntries.File("a.txt")] }, deleter, new TestNavigation());
        vm.MenuTarget = vm.Items[0];

        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsDeleteNoticeOpen);
        Assert.IsFalse(vm.CanUndoDelete);
    }

    [TestMethod]
    public async Task DeleteTargets_Fails_ReportsStatusAndNoNotice()
    {
        TestDeleter deleter = new() { Error = new IOException("in use") };
        TestErrors errors = new();
        TestFolders folders = new() { Entries = [TestEntries.File("a.txt")] };
        FolderViewModel vm = new(folders, new TestFileActions(), deleter, new TestResources(), new TestWatcher(), new TestNavigation(),
            new TestTextProvider(), TestTime.Create(), errors);
        using (vm)
        {
            vm.OnShown();
            await vm.RefreshCommand.ExecutionTask!;
            vm.MenuTarget = vm.Items[0];

            await vm.DeleteTargetsCommand.ExecuteAsync(null);

            Assert.IsFalse(vm.IsDeleteNoticeOpen);
            Assert.IsTrue(vm.IsStatusOpen);
            Assert.AreEqual(1, errors.Exceptions.Count);
        }
    }

    [TestMethod]
    public async Task Notice_AfterUndoWindow_Disappears()
    {
        FakeTimeProvider time = TestTime.Create();
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt")] }, new TestDeleter(), new TestNavigation(), time);
        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        time.Advance(FolderViewModel.UndoWindow - TimeSpan.FromSeconds(1));
        Assert.IsTrue(vm.IsDeleteNoticeOpen);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.IsFalse(vm.IsDeleteNoticeOpen);
        Assert.IsFalse(vm.CanUndoDelete);
    }

    [TestMethod]
    public async Task UndoDelete_RestoresTheDeletedItems()
    {
        TestDeleter deleter = new();
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] }, deleter, new TestNavigation());
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);
        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        await vm.UndoDeleteCommand.ExecuteAsync(null);

        Assert.AreEqual(2, deleter.Restores.Single().Count);
        Assert.IsFalse(vm.IsDeleteNoticeOpen);
        Assert.AreEqual("Restored 2 items", vm.CountText);
    }

    [TestMethod]
    public async Task UndoDelete_PartlyRestored_SaysSo()
    {
        TestDeleter deleter = new() { RestoredCount = 1 };
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] }, deleter, new TestNavigation());
        vm.SelectedItems.Add(vm.Items[0]);
        vm.SelectedItems.Add(vm.Items[1]);
        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);

        await vm.UndoDeleteCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsStatusOpen);
    }

    [TestMethod]
    public async Task Drag_ShowsBinAndHidesNoticeUntilDragEnds()
    {
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] }, new TestDeleter(), new TestNavigation());
        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsDeleteNoticeShown);

        vm.BeginDrag([vm.Items[0]]);
        Assert.IsTrue(vm.IsDragging);
        Assert.IsFalse(vm.IsDeleteNoticeShown);

        vm.EndDrag();
        Assert.IsFalse(vm.IsDragging);
        Assert.IsTrue(vm.IsDeleteNoticeShown);
    }

    [TestMethod]
    public async Task DeleteDragged_DeletesTheDraggedRows()
    {
        TestDeleter deleter = new();
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt"), TestEntries.File("b.txt")] }, deleter, new TestNavigation());
        vm.BeginDrag([vm.Items[1]]);

        await vm.DeleteDraggedCommand.ExecuteAsync(null);

        Assert.AreEqual("b.txt", deleter.Deletes.Single().Single().Name);
    }

    [TestMethod]
    public async Task Hiding_DismissesNoticeAndDrag()
    {
        using FolderViewModel vm = await ShowAsync(
            new TestFolders { Entries = [TestEntries.File("a.txt")] }, new TestDeleter(), new TestNavigation());
        vm.MenuTarget = vm.Items[0];
        await vm.DeleteTargetsCommand.ExecuteAsync(null);
        vm.BeginDrag([vm.Items[0]]);

        vm.OnHidden();

        Assert.IsFalse(vm.IsDeleteNoticeOpen);
        Assert.IsFalse(vm.IsDragging);
    }
}
