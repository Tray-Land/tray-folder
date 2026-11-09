using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using TrayFolder.Core;

namespace TrayFolder.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly ITextProvider _text;
    private readonly IFileResources _resources;
    private readonly IErrorReporter _errors;
    private readonly LatestRequest _thumbnailRequest = new();
    private Task? _resolving;
    private CancellationToken _resolvingToken;
    private uint _thumbnailSize;

    public FileItemViewModel(FileEntry entry, DateTime now, ITextProvider text, IFileResources resources, IErrorReporter errors)
    {
        Entry = entry;
        _text = text;
        _resources = resources;
        _errors = errors;
        RefreshDate(now);
    }

    public FileEntry Entry { get; }

    public string FullPath => Entry.FullPath;

    public bool IsFolder => Entry.IsFolder;

    public string Glyph => FileKinds.Glyph(Entry.Kind);

    public double ItemOpacity => Entry.IsHidden ? 0.6 : 1.0;

    public string DisplayName => ShowExtension || IsFolder ? Entry.Name : Path.GetFileNameWithoutExtension(Entry.Name);

    public string SizeText => IsFolder ? string.Empty : FolderViewPolicy.FormatSize(Entry.Size);

    public string Details => IsFolder
        ? _text.Format("FolderDetails", ModifiedText)
        : _text.Format("FileDetails", ModifiedText, SizeText);

    /// <summary>The IStorageItem to drag, once resolved. Opaque; see <see cref="IFileResources"/>.</summary>
    public object? StorageItem { get; private set; }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Audio files show their cover art as the player's poster.</summary>
    public object? AudioPoster => Entry.Kind == FileKind.Audio ? Thumbnail : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial bool ShowExtension { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial string ModifiedText { get; private set; } = string.Empty;

    /// <summary>An ImageSource of the shell thumbnail, once loaded. Opaque; see <see cref="IFileResources"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail), nameof(AudioPoster))]
    public partial object? Thumbnail { get; private set; }

    public void RefreshDate(DateTime now) =>
        ModifiedText = FileDateFormatter.Format(Entry.Modified, now, CultureInfo.CurrentCulture, _text);

    /// <summary>Resolves <see cref="StorageItem"/> ahead of a drag, unless that is done or already under way.</summary>
    public Task PrefetchAsync(CancellationToken cancellationToken)
    {
        if (StorageItem is not null || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (_resolving is { IsCompleted: false } && !_resolvingToken.IsCancellationRequested)
        {
            return _resolving;
        }

        _resolvingToken = cancellationToken;
        return _resolving = ResolveAsync(cancellationToken);
    }

    /// <summary>Loads the thumbnail, unless one at least this big is already here.</summary>
    public async Task LoadThumbnailAsync(uint size, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _thumbnailSize >= size)
        {
            return;
        }

        using LatestRequest.Scope request = _thumbnailRequest.Begin(cancellationToken);
        CancellationToken token = request.Token;
        try
        {
            _ = PrefetchAsync(cancellationToken);
            object? thumbnail = await _resources.LoadThumbnailAsync(FullPath, size, token);
            token.ThrowIfCancellationRequested();
            if (thumbnail is not null)
            {
                Thumbnail = thumbnail;
                _thumbnailSize = size;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _errors.Report("Load shell thumbnail", ex);
        }
    }

    public override string ToString() => _text.Format("FileAccessibleName", Entry.Name, Details);

    private async Task ResolveAsync(CancellationToken token)
    {
        try
        {
            object item = await _resources.ResolveAsync(Entry, token);
            token.ThrowIfCancellationRequested();
            StorageItem = item;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _errors.Report("Resolve file storage item", ex);
        }
    }
}
