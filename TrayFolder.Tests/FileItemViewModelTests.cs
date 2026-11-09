using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

[TestClass]
public sealed class FileItemViewModelTests
{
    [TestMethod]
    public async Task LoadThumbnail_Loaded_PublishesAndNotifies()
    {
        object image = new();
        TestResources resources = new() { Thumbnail = image };
        FileItemViewModel item = TestEntries.Item("a.txt", resources);
        List<string?> notifications = [];
        item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        await item.LoadThumbnailAsync(32, CancellationToken.None);

        Assert.AreSame(image, item.Thumbnail);
        Assert.IsTrue(item.HasThumbnail);
        CollectionAssert.Contains(notifications, nameof(FileItemViewModel.HasThumbnail));
    }

    [TestMethod]
    public async Task LoadThumbnail_AlreadyBigEnough_DoesNotLoadAgain()
    {
        TestResources resources = new();
        FileItemViewModel item = TestEntries.Item("a.txt", resources);

        await item.LoadThumbnailAsync(96, CancellationToken.None);
        await item.LoadThumbnailAsync(32, CancellationToken.None);
        await item.LoadThumbnailAsync(96, CancellationToken.None);

        Assert.AreEqual(1, resources.ThumbnailCalls.Count);
    }

    [TestMethod]
    public async Task LoadThumbnail_ShellHasNone_KeepsTheGlyph()
    {
        TestResources resources = new() { Thumbnail = null };
        FileItemViewModel item = TestEntries.Item("a.txt", resources);

        await item.LoadThumbnailAsync(32, CancellationToken.None);

        Assert.IsFalse(item.HasThumbnail);
    }

    [TestMethod]
    public async Task LoadThumbnail_OlderRequestFinishesLast_KeepsTheNewest()
    {
        TestResources resources = new();
        TaskCompletionSource<object?> small = new();
        TaskCompletionSource<object?> large = new();
        resources.PendingThumbnails.Enqueue(small);
        resources.PendingThumbnails.Enqueue(large);
        FileItemViewModel item = TestEntries.Item("a.txt", resources);
        object largeImage = new();

        Task first = item.LoadThumbnailAsync(32, CancellationToken.None);
        Task second = item.LoadThumbnailAsync(96, CancellationToken.None);
        large.SetResult(largeImage);
        await second;
        small.SetResult(new object());
        await first;

        Assert.IsTrue(resources.ThumbnailCalls[0].Token.IsCancellationRequested);
        Assert.AreSame(largeImage, item.Thumbnail);
    }

    [TestMethod]
    public async Task LoadThumbnail_CanceledBeforeItStarts_DoesNothing()
    {
        TestResources resources = new();
        FileItemViewModel item = TestEntries.Item("a.txt", resources);

        await item.LoadThumbnailAsync(32, new CancellationToken(canceled: true));

        Assert.AreEqual(0, resources.ThumbnailCalls.Count);
    }

    [TestMethod]
    public async Task LoadThumbnail_AlsoGetsTheRowReadyToDrag()
    {
        TestResources resources = new();
        FileItemViewModel item = TestEntries.Item("a.txt", resources);

        await item.LoadThumbnailAsync(32, CancellationToken.None);

        Assert.IsNotNull(item.StorageItem);
    }

    [TestMethod]
    public async Task Prefetch_CalledTwiceWhileResolving_ResolvesOnce()
    {
        TestResources resources = new() { PendingResolve = new TaskCompletionSource<object>() };
        FileItemViewModel item = TestEntries.Item("a.txt", resources);

        Task first = item.PrefetchAsync(CancellationToken.None);
        Task second = item.PrefetchAsync(CancellationToken.None);
        resources.PendingResolve.SetResult("resolved");
        await Task.WhenAll(first, second);
        await item.PrefetchAsync(CancellationToken.None);

        Assert.AreEqual(1, resources.Resolved.Count);
        Assert.AreEqual("resolved", item.StorageItem);
    }

    [TestMethod]
    public async Task Prefetch_FirstAttemptCanceled_LaterOneResolves()
    {
        TestResources resources = new() { PendingResolve = new TaskCompletionSource<object>() };
        FileItemViewModel item = TestEntries.Item("a.txt", resources);
        using CancellationTokenSource hidden = new();

        Task first = item.PrefetchAsync(hidden.Token);
        hidden.Cancel();
        Task second = item.PrefetchAsync(CancellationToken.None);
        resources.PendingResolve.SetResult("resolved");
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, resources.Resolved.Count);
        Assert.AreEqual("resolved", item.StorageItem);
    }

    [TestMethod]
    public async Task Prefetch_Fails_ReportsAndCanRetry()
    {
        IOException error = new("Locked");
        TestErrors errors = new();
        TestResources resources = new() { ResolveError = error };
        FileItemViewModel item = TestEntries.Item("a.txt", resources, errors);

        await item.PrefetchAsync(CancellationToken.None);

        Assert.IsNull(item.StorageItem);
        Assert.AreSame(error, errors.Exceptions.Single());

        resources.ResolveError = null;
        await item.PrefetchAsync(CancellationToken.None);

        Assert.IsNotNull(item.StorageItem);
    }

    [TestMethod]
    public async Task AudioPoster_IsTheThumbnailOnlyForAudio()
    {
        TestResources resources = new();
        FileItemViewModel song = TestEntries.Item("song.mp3", resources);
        FileItemViewModel text = TestEntries.Item("note.txt", resources);

        await song.LoadThumbnailAsync(32, CancellationToken.None);
        await text.LoadThumbnailAsync(32, CancellationToken.None);

        Assert.AreSame(song.Thumbnail, song.AudioPoster);
        Assert.IsNull(text.AudioPoster);
    }

    [TestMethod]
    public void DisplayName_HidingExtensions_KeepsFolderNamesWhole()
    {
        FileItemViewModel file = TestEntries.Item("a.txt");
        FileItemViewModel folder = new(TestEntries.Folder("b.c"), new DateTime(2026, 11, 3), new TestTextProvider(), new TestResources(), new TestErrors());

        file.ShowExtension = false;
        folder.ShowExtension = false;

        Assert.AreEqual("a", file.DisplayName);
        Assert.AreEqual("b.c", folder.DisplayName);
    }
}
