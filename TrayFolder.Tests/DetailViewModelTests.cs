using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

[TestClass]
public sealed class DetailViewModelTests
{
    private static DetailViewModel Create(TestPreview preview, TestFileActions? files = null, TestNavigation? navigation = null) =>
        new(preview, files ?? new TestFileActions(), navigation ?? new TestNavigation(), new TestTextProvider(), new TestErrors());

    [TestMethod]
    public async Task Show_SlowerPreviousSvgFinishesLast_KeepsCurrentFileMetadata()
    {
        TestPreview preview = new();
        TaskCompletionSource<PreviewResult> old = new();
        preview.Loads.Enqueue(old);
        FileItemViewModel first = TestEntries.Item("first.svg");
        FileItemViewModel second = TestEntries.Item("second.svg");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();
        Task initial = vm.ShowAsync(first, [first, second]);

        await vm.ShowAsync(second, [first, second]);
        old.SetResult(new PreviewResult("Old SVG", "old dimensions"));
        await initial;

        Assert.IsTrue(preview.LoadCalls[0].Token.IsCancellationRequested);
        Assert.AreSame(second, vm.Item);
        Assert.AreEqual("second.svg", vm.Name);
        Assert.AreEqual("Text", vm.Information[0].Value);
        Assert.IsFalse(vm.LoadCommand.IsRunning);
    }

    [TestMethod]
    public async Task Show_LoadedContent_PublishesItForTheView()
    {
        object image = new();
        TestPreview preview = new() { Result = new PreviewResult("PNG", "2 × 2", Image: image) };
        FileItemViewModel item = TestEntries.Item("a.png");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();

        await vm.ShowAsync(item, [item]);

        Assert.AreSame(image, vm.Preview.Image);
        Assert.IsTrue(vm.Preview.HasImage);
        Assert.IsFalse(vm.HasNoPreview);
        Assert.AreEqual("2 × 2", vm.Information[1].Value);
    }

    [TestMethod]
    public async Task Show_NoContent_OffersDragAndShowsTheStandIn()
    {
        object thumbnail = new();
        TestPreview preview = new() { Result = new PreviewResult("ZIP", Thumbnail: thumbnail) };
        FileItemViewModel item = TestEntries.Item("a.zip");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();

        await vm.ShowAsync(item, [item]);

        Assert.IsTrue(vm.HasNoPreview);
        Assert.IsTrue(vm.CanDragPreview);
        Assert.IsTrue(vm.Preview.HasThumbnail);
        Assert.AreEqual("No preview available", vm.PreviewMessage);
    }

    [TestMethod]
    public async Task Show_MissingFile_SaysItMoved()
    {
        TestPreview preview = new();
        TaskCompletionSource<PreviewResult> load = new();
        load.SetException(new FileNotFoundException());
        preview.Loads.Enqueue(load);
        FileItemViewModel item = TestEntries.Item("gone.txt");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();

        await vm.ShowAsync(item, [item]);

        Assert.IsTrue(vm.HasNoPreview);
        Assert.AreEqual("This file was moved or deleted", vm.PreviewMessage);
        Assert.IsTrue(vm.HasError);
    }

    [TestMethod]
    public async Task Show_LoadsTheHeaderThumbnail()
    {
        TestResources resources = new();
        FileItemViewModel item = TestEntries.Item("a.txt", resources);
        using DetailViewModel vm = Create(new TestPreview());
        vm.OnShown();

        await vm.ShowAsync(item, [item]);

        Assert.AreEqual(32u, resources.ThumbnailCalls.Single().Size);
        Assert.IsTrue(vm.Item!.HasThumbnail);
    }

    [TestMethod]
    public async Task PreviousNext_FollowPositionAmongSiblings()
    {
        FileItemViewModel first = TestEntries.Item("first.txt");
        FileItemViewModel second = TestEntries.Item("second.txt");
        using DetailViewModel vm = Create(new TestPreview());
        vm.OnShown();

        await vm.ShowAsync(first, [first, second]);
        Assert.IsFalse(vm.PreviousCommand.CanExecute(null));
        Assert.IsTrue(vm.NextCommand.CanExecute(null));

        await vm.NextCommand.ExecuteAsync(null);
        Assert.AreSame(second, vm.Item);
        Assert.IsTrue(vm.PreviousCommand.CanExecute(null));
        Assert.IsFalse(vm.NextCommand.CanExecute(null));

        await vm.PreviousCommand.ExecuteAsync(null);
        Assert.AreSame(first, vm.Item);
    }

    [TestMethod]
    public async Task NextPage_OlderRenderFinishesLast_PublishesLatestPageCounter()
    {
        TestPreview preview = new() { Result = new PreviewResult("PDF", PageCount: 4, Image: new object()) };
        TaskCompletionSource<object> old = new();
        TaskCompletionSource<object> recent = new();
        preview.Renders.Enqueue(old);
        preview.Renders.Enqueue(recent);
        FileItemViewModel item = TestEntries.Item("pages.pdf");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();
        await vm.ShowAsync(item, [item]);

        Task first = vm.NextPageCommand.ExecuteAsync(null);
        Task second = vm.NextPageCommand.ExecuteAsync(null);
        object latest = new();
        recent.SetResult(latest);
        await second;
        old.SetResult(new object());
        await first;

        Assert.IsTrue(preview.RenderCalls[0].Token.IsCancellationRequested);
        Assert.AreEqual(2u, vm.PageIndex);
        Assert.AreEqual("3 / 4", vm.PageText);
        Assert.AreSame(latest, vm.Preview.Image);
    }

    [TestMethod]
    public async Task OnHidden_PreviewInFlight_CancelsAndDoesNotPublish()
    {
        TestPreview preview = new();
        TaskCompletionSource<PreviewResult> pending = new();
        preview.Loads.Enqueue(pending);
        FileItemViewModel item = TestEntries.Item("file.svg");
        using DetailViewModel vm = Create(preview);
        int pauses = 0;
        vm.PauseMediaRequested += (_, _) => pauses++;
        vm.OnShown();
        Task load = vm.ShowAsync(item, [item]);

        vm.OnHidden();
        pending.SetResult(new PreviewResult("Hidden SVG", "hidden dimensions"));
        await load;

        Assert.IsTrue(preview.LoadCalls.Single().Token.IsCancellationRequested);
        Assert.AreEqual(1, pauses);
        Assert.AreEqual("File", vm.Information[0].Value);
        Assert.IsFalse(vm.LoadCommand.IsRunning);
    }

    [TestMethod]
    public async Task Open_PausesPlaybackFirst()
    {
        TestFileActions files = new();
        FileItemViewModel item = TestEntries.Item("song.mp3");
        using DetailViewModel vm = Create(new TestPreview(), files);
        int pauses = 0;
        vm.PauseMediaRequested += (_, _) => pauses++;
        vm.OnShown();
        await vm.ShowAsync(item, [item]);

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.AreEqual(1, pauses);
        Assert.AreEqual(item.FullPath, files.Opens.Single());
    }

    [TestMethod]
    public async Task Dispose_ClearsPublishedContentThenReleasesIt()
    {
        TestPreview preview = new() { Result = new PreviewResult("MP4", Media: new object()) };
        FileItemViewModel item = TestEntries.Item("a.mp4");
        DetailViewModel vm = Create(preview);
        vm.OnShown();
        await vm.ShowAsync(item, [item]);
        int clearCallsWhenEmptied = -1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetailViewModel.Preview) && !vm.Preview.HasMedia)
            {
                clearCallsWhenEmptied = preview.ClearCalls;
            }
        };
        int clearCallsBefore = preview.ClearCalls;

        vm.Dispose();

        Assert.AreEqual(clearCallsBefore, clearCallsWhenEmptied, "The view must let go of the media before it is released.");
        Assert.AreEqual(clearCallsBefore + 1, preview.ClearCalls);
    }

    [TestMethod]
    public async Task NextPage_FailedRender_RestoresLastRenderedPage()
    {
        TestPreview preview = new() { Result = new PreviewResult("PDF", PageCount: 3, Image: new object()) };
        TaskCompletionSource<object> render = new();
        preview.Renders.Enqueue(render);
        FileItemViewModel item = TestEntries.Item("file.pdf");
        using DetailViewModel vm = Create(preview);
        vm.OnShown();
        await vm.ShowAsync(item, [item]);

        Task pending = vm.NextPageCommand.ExecuteAsync(null);
        render.SetException(new IOException("Cannot render"));
        await pending;

        Assert.AreEqual(0u, vm.PageIndex);
        Assert.AreEqual("1 / 3", vm.PageText);
        StringAssert.Contains(vm.ErrorMessage, "Cannot render");
    }

    [TestMethod]
    public async Task Copy_FileChangesBeforeCompletion_DoesNotMarkNewFileCopied()
    {
        TestPreview preview = new();
        TaskCompletionSource copy = new();
        TestFileActions files = new() { PendingCopy = copy };
        FileItemViewModel first = TestEntries.Item("first.txt");
        FileItemViewModel second = TestEntries.Item("second.txt");
        using DetailViewModel vm = Create(preview, files);
        vm.OnShown();
        await vm.ShowAsync(first, [first, second]);
        Task pending = vm.CopyCommand.ExecuteAsync(null);

        await vm.ShowAsync(second, [first, second]);
        copy.SetResult();
        await pending;

        Assert.AreSame(first.Entry, files.Copies.Single().Single());
        Assert.AreEqual("Copy", vm.CopyText);
    }

    [TestMethod]
    public async Task Cut_PutsTheFileOnTheClipboardToMove()
    {
        TestFileActions files = new();
        FileItemViewModel item = TestEntries.Item("a.txt");
        using DetailViewModel vm = Create(new TestPreview(), files);
        vm.OnShown();
        await vm.ShowAsync(item, [item]);

        await vm.CutCommand.ExecuteAsync(null);

        Assert.AreSame(item.Entry, files.Cuts.Single().Single());
        Assert.AreEqual("Cut", vm.CopyText);
    }
}
