using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using TrayFolder.Core;

namespace TrayFolder.ViewModels;

public sealed partial class DetailViewModel : VisibleViewModel
{
    private const uint HeaderThumbnailSize = 32;

    private readonly IFilePreview _preview;
    private readonly IFileActions _files;
    private readonly IFlyoutNavigation _navigation;
    private readonly LatestRequest _load = new();
    private readonly LatestRequest _page = new();
    private List<FileItemViewModel> _siblings = [];

    public DetailViewModel(IFilePreview preview, IFileActions files, IFlyoutNavigation navigation,
        ITextProvider text, IErrorReporter errors) : base(text, errors)
    {
        _preview = preview;
        _files = files;
        _navigation = navigation;
        CopyText = text.Get("Copy");
    }

    /// <summary>Playback belongs to the view's media element, which should pause when this fires.</summary>
    public event EventHandler? PauseMediaRequested;

    public string Name => Item?.Entry.Name ?? string.Empty;

    public bool CanPrevious => Index > 0;

    public bool CanNext => Index >= 0 && Index < _siblings.Count - 1;

    public bool ShowPdfPager => Preview.PageCount > 1;

    public bool CanPreviousPage => ShowPdfPager && PageIndex > 0;

    public bool CanNextPage => ShowPdfPager && PageIndex + 1 < Preview.PageCount;

    public string PageText => Text.Format("PdfPage", RenderedPageIndex + 1, Preview.PageCount);

    private int Index => Item is null ? -1 : _siblings.IndexOf(Item);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(CanPrevious), nameof(CanNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial FileItemViewModel? Item { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPdfPager), nameof(CanPreviousPage), nameof(CanNextPage), nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial PreviewResult Preview { get; private set; } = PreviewResult.Empty;

    /// <summary>The page last asked for; it can be ahead of <see cref="RenderedPageIndex"/> while rendering.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviousPage), nameof(CanNextPage))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial uint PageIndex { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial uint RenderedPageIndex { get; private set; }

    [ObservableProperty]
    public partial bool CanDragPreview { get; private set; }

    [ObservableProperty]
    public partial bool HasNoPreview { get; private set; }

    [ObservableProperty]
    public partial string PreviewMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CopyText { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<FileInformation> Information { get; private set; } = [];

    public Task ShowAsync(FileItemViewModel item, IReadOnlyList<FileItemViewModel> siblings)
    {
        _siblings = [.. siblings];
        return LoadCommand.ExecuteAsync(item);
    }

    public override void Dispose()
    {
        base.Dispose();
        ClearPreview();
    }

    protected override void OnDeactivated() => PauseMediaRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LoadAsync(FileItemViewModel item)
    {
        if (!IsVisible)
        {
            return;
        }

        _page.Cancel();
        using LatestRequest.Scope request = _load.Begin(VisibleToken);
        CancellationToken token = request.Token;
        ClearPreview();
        Item = item;
        _ = item.LoadThumbnailAsync(HeaderThumbnailSize, VisibleToken);
        CopyText = Text.Get("Copy");
        ErrorMessage = string.Empty;
        PageIndex = 0;
        RenderedPageIndex = 0;
        HasNoPreview = false;
        CanDragPreview = FileKinds.PreviewOf(item.Entry.Extension, item.Entry.Size) is not (PreviewKind.Text or PreviewKind.Media);
        UpdateInformation(item, new PreviewResult(Text.Get("FileType")));
        try
        {
            PreviewResult result = await _preview.LoadAsync(item.Entry, token);
            token.ThrowIfCancellationRequested();
            Preview = result;
            HasNoPreview = !result.HasContent;
            PreviewMessage = Text.Get("NoPreview");
            CanDragPreview |= HasNoPreview;
            UpdateInformation(item, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportFailure("Load file preview", ex, token);
            if (!token.IsCancellationRequested)
            {
                ClearPreview();
                HasNoPreview = true;
                CanDragPreview = true;
                PreviewMessage = Text.Get(ex is FileNotFoundException ? "FileMoved" : "PreviewFailed");
            }
        }
    }

    [RelayCommand]
    private void Back() => _navigation.ShowMainPage();

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousAsync() => LoadCommand.ExecuteAsync(_siblings[Index - 1]);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => LoadCommand.ExecuteAsync(_siblings[Index + 1]);

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => RenderPageAsync(PageIndex - 1);

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => RenderPageAsync(PageIndex + 1);

    [RelayCommand]
    private Task OpenAsync() => RunFileActionAsync((item, token) =>
    {
        PauseMediaRequested?.Invoke(this, EventArgs.Empty);
        return _files.OpenAsync(item.FullPath, token);
    });

    [RelayCommand]
    private Task OpenWithAsync() => RunFileActionAsync((item, token) => _files.OpenWithAsync(item.FullPath, token));

    [RelayCommand]
    private Task CopyPathsAsync() => RunFileActionAsync((item, token) => _files.CopyPathsAsync(new[] { item.Entry }, token));

    [RelayCommand]
    private Task ShowInExplorerAsync() => RunFileActionAsync((item, token) => _files.ShowInExplorerAsync(item.FullPath, token));

    [RelayCommand]
    private Task CopyAsync() => RunFileActionAsync(async (item, token) =>
    {
        await _files.CopyAsync(new[] { item.Entry }, token);
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(Item, item))
        {
            CopyText = Text.Get("Copied");
        }
    });

    private async Task RenderPageAsync(uint index)
    {
        using LatestRequest.Scope request = _page.Begin(VisibleToken);
        CancellationToken token = request.Token;
        PageIndex = index;
        try
        {
            object image = await _preview.RenderPageAsync(index, token);
            token.ThrowIfCancellationRequested();
            Preview = Preview with { Image = image };
            RenderedPageIndex = index;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportFailure("Render PDF page", ex, token);
            if (!token.IsCancellationRequested)
            {
                PageIndex = RenderedPageIndex;
            }
        }
    }

    private Task RunFileActionAsync(Func<FileItemViewModel, CancellationToken, Task> action) =>
        Item is { } item ? RunAsync("File action", token => action(item, token)) : Task.CompletedTask;

    private void ClearPreview()
    {
        // In this order, so the view lets go of the media before it is disposed.
        Preview = PreviewResult.Empty;
        _preview.Clear();
    }

    private void UpdateInformation(FileItemViewModel item, PreviewResult result)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        List<FileInformation> information = [new(Text.Get("TypeLabel"), result.TypeName)];
        if (result.Dimensions is not null)
        {
            information.Add(new(Text.Get(item.Entry.Kind == FileKind.Document ? "PagesLabel" : "DimensionsLabel"), result.Dimensions));
        }

        information.Add(new(Text.Get("SizeLabel"), item.Entry.Size < 1024 ? item.SizeText :
            Text.Format("SizeWithBytes", item.SizeText, item.Entry.Size.ToString("N0", culture))));
        information.Add(new(Text.Get("ModifiedLabel"), item.Entry.Modified.ToString("f", culture)));
        information.Add(new(Text.Get("CreatedLabel"), item.Entry.Created.ToString("f", culture)));
        information.Add(new(Text.Get("LocationLabel"), Path.GetDirectoryName(item.FullPath) ?? item.FullPath));
        Information = information;
    }
}
