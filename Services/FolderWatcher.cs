using Microsoft.UI.Dispatching;
using TrayFolder.Core;

namespace TrayFolder.Services;

/// <summary>
/// Coalesces bursts of changes in a folder (a browser writing a download fires dozens) into one
/// <see cref="Changed"/> on the UI thread.
/// </summary>
internal sealed class FolderWatcher : IFolderWatcher
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private FileSystemWatcher? _watcher;

    public FolderWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = Debounce;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public void Watch(string path)
    {
        Stop();
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Error += OnChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Some locations (network shares, removed drives) can't be watched; the flyout still
            // refreshes every time it opens.
            Stop();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // Raised on a thread-pool thread; the timer belongs to the UI thread.
    private void OnChanged(object sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (_watcher is not null)
        {
            _timer.Stop();
            _timer.Start();
        }
    });
}
