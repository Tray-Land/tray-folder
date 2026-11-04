using Microsoft.Extensions.Time.Testing;
using System.Xml.Linq;
using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

internal sealed class TestTextProvider : ITextProvider
{
    private readonly Dictionary<string, string> _strings = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources.resw"))
        .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    public string Get(string key) => _strings[key];
}

internal sealed class TestErrors : IErrorReporter
{
    public List<Exception> Exceptions { get; } = [];

    public void Report(string operation, Exception exception) => Exceptions.Add(exception);
}

internal static class TestTime
{
    /// <summary>Noon on a known day, in UTC. Time moves only when a test calls Advance.</summary>
    public static FakeTimeProvider Create()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }
}

internal sealed class TestFolders : IFolderRepository
{
    public string RootPath { get; private set; } = @"C:\files";
    public string DownloadsPath => @"C:\Downloads";
    public IReadOnlyList<string> RecentFolders { get; set; } = [];
    public FolderViewOptions ViewOptions { get; set; } = new() { GroupBy = GroupField.None };
    public event EventHandler? RootChanged;
    public IReadOnlyList<FileEntry> Entries { get; set; } = [];
    public Queue<TaskCompletionSource<IReadOnlyList<FileEntry>>> PendingReads { get; } = [];
    public List<CancellationToken> ReadTokens { get; } = [];
    public List<string> ReadPaths { get; } = [];
    public Exception? ReadError { get; set; }
    public bool HasRootChangedSubscribers => RootChanged is not null;

    public void SetRoot(string path)
    {
        RootPath = path;
        RootChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRecent() => RecentFolders = [];

    public bool DirectoryExists(string path) => true;

    public Task<IReadOnlyList<FileEntry>> EnumerateAsync(string path, CancellationToken cancellationToken)
    {
        ReadPaths.Add(path);
        ReadTokens.Add(cancellationToken);
        return ReadError is { } error ? Task.FromException<IReadOnlyList<FileEntry>>(error) :
            PendingReads.Count > 0 ? PendingReads.Dequeue().Task : Task.FromResult(Entries);
    }

    public Task<IReadOnlyList<FolderChoice>> GetFolderChoicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FolderChoice>>([new("Files", RootPath, "folder")]);
}

internal sealed class TestWatcher : IFolderWatcher
{
    public event EventHandler? Changed;
    public string? WatchedPath { get; private set; }
    public void Watch(string path) => WatchedPath = path;
    public void Stop() => WatchedPath = null;
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class TestNavigation : IFlyoutNavigation
{
    public FileItemViewModel? DetailItem { get; private set; }
    public IReadOnlyList<FileItemViewModel> DetailSiblings { get; private set; } = [];
    public int BackCalls { get; private set; }
    public int SettingsCalls { get; private set; }
    public string? PickedFolder { get; set; }
    public void ShowMainPage() => BackCalls++;
    public void ShowSettingsPage() => SettingsCalls++;
    public void ShowDetailPage(FileItemViewModel item, IReadOnlyList<FileItemViewModel> siblings)
    {
        DetailItem = item;
        DetailSiblings = siblings;
    }
    public Task<string?> PickFolderAsync(string startFolder, CancellationToken cancellationToken) => Task.FromResult(PickedFolder);
}

internal sealed class TestFileActions : IFileActions
{
    public TaskCompletionSource? PendingCopy { get; set; }
    public List<IReadOnlyList<FileEntry>> Copies { get; } = [];
    public List<IReadOnlyList<FileEntry>> PathCopies { get; } = [];
    public List<string> Opens { get; } = [];
    public List<string> OpenWiths { get; } = [];
    public List<string> Shown { get; } = [];
    public Exception? Error { get; set; }
    public Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        Opens.Add(path);
        return Error is { } error ? Task.FromException(error) : Task.CompletedTask;
    }
    public Task OpenWithAsync(string path, CancellationToken cancellationToken) { OpenWiths.Add(path); return Task.CompletedTask; }
    public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) { Shown.Add(path); return Task.CompletedTask; }
    public Task OpenFolderAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CopyAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        Copies.Add(entries);
        return PendingCopy?.Task ?? Task.CompletedTask;
    }
    public List<IReadOnlyList<FileEntry>> Cuts { get; } = [];
    public Task CutAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        Cuts.Add(entries);
        return Task.CompletedTask;
    }
    public Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        PathCopies.Add(entries);
        return Task.CompletedTask;
    }
}

internal sealed class TestResources : IFileResources
{
    public List<string> Resolved { get; } = [];
    public List<(string Path, uint Size, CancellationToken Token)> ThumbnailCalls { get; } = [];
    public Queue<TaskCompletionSource<object?>> PendingThumbnails { get; } = [];
    public TaskCompletionSource<object>? PendingResolve { get; set; }
    public Exception? ResolveError { get; set; }
    public object? Thumbnail { get; set; } = new();

    public Task<object> ResolveAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        Resolved.Add(entry.FullPath);
        return ResolveError is { } error ? Task.FromException<object>(error) :
            PendingResolve?.Task ?? Task.FromResult<object>(entry.FullPath);
    }

    public Task<object?> LoadThumbnailAsync(string path, uint size, CancellationToken cancellationToken)
    {
        ThumbnailCalls.Add((path, size, cancellationToken));
        return PendingThumbnails.Count > 0 ? PendingThumbnails.Dequeue().Task : Task.FromResult(Thumbnail);
    }
}

internal sealed class TestStartup : IStartupService
{
    public StartupState State { get; set; } = StartupState.Disabled;
    public Queue<TaskCompletionSource<StartupState>> Reads { get; } = [];
    public Queue<TaskCompletionSource<StartupState>> Writes { get; } = [];
    public List<(bool Enabled, CancellationToken Token)> WriteCalls { get; } = [];
    public List<CancellationToken> ReadTokens { get; } = [];
    public Task<StartupState> GetStateAsync(CancellationToken cancellationToken)
    {
        ReadTokens.Add(cancellationToken);
        return Reads.Count > 0 ? Reads.Dequeue().Task : Task.FromResult(State);
    }
    public Task<StartupState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        WriteCalls.Add((enabled, cancellationToken));
        return Writes.Dequeue().Task;
    }
}

internal sealed class TestHotkey : IHotkeyService
{
    public HotkeyGesture? Gesture { get; private set; } = HotkeyGesture.Default;
    public HotkeyResult Status { get; set; } = HotkeyResult.Registered;
    public HotkeyResult Set(HotkeyGesture? gesture)
    {
        Gesture = gesture;
        return gesture is null ? HotkeyResult.Disabled : Status;
    }
}

internal sealed class TestPreview : IFilePreview
{
    public Queue<TaskCompletionSource<PreviewResult>> Loads { get; } = [];
    public Queue<TaskCompletionSource<object>> Renders { get; } = [];
    public List<(FileEntry Entry, CancellationToken Token)> LoadCalls { get; } = [];
    public List<(uint Index, CancellationToken Token)> RenderCalls { get; } = [];
    public PreviewResult Result { get; set; } = new("Text", Text: "content");
    public int ClearCalls { get; private set; }
    public Task<PreviewResult> LoadAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        LoadCalls.Add((entry, cancellationToken));
        return Loads.Count > 0 ? Loads.Dequeue().Task : Task.FromResult(Result);
    }
    public Task<object> RenderPageAsync(uint pageIndex, CancellationToken cancellationToken)
    {
        RenderCalls.Add((pageIndex, cancellationToken));
        return Renders.Count > 0 ? Renders.Dequeue().Task : Task.FromResult(new object());
    }
    public void Clear() => ClearCalls++;
}

internal static class TestEntries
{
    public static FileEntry File(string name, DateTime? modified = null) =>
        new(name, $@"C:\files\{name}", false, 1000, modified ?? new DateTime(2026, 11, 3, 9, 0, 0),
            new DateTime(2026, 11, 3), false);

    public static FileEntry Folder(string name) =>
        new(name, $@"C:\files\{name}", true, 0, new DateTime(2026, 11, 3, 9, 0, 0), new DateTime(2026, 11, 3), false);

    public static FileItemViewModel Item(string name, TestResources? resources = null, TestErrors? errors = null) =>
        new(File(name), new DateTime(2026, 11, 3), new TestTextProvider(), resources ?? new TestResources(), errors ?? new TestErrors());
}
