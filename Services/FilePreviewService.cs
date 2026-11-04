using Microsoft.UI.Xaml.Media.Imaging;
using TrayFolder.Core;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace TrayFolder.Services;

/// <summary>The space a preview has on screen: its size in physical pixels, and physical pixels per layout pixel.</summary>
internal readonly record struct PreviewViewport(int Width, int Height, double Scale);

/// <summary>Decodes a file into something the detail page can show. UI thread only: image sources belong to it.</summary>
internal sealed class FilePreviewService(ITextProvider text, Func<PreviewViewport> viewport) : IFilePreview
{
    private const int NoPreviewThumbnailSize = 128;

    private PdfDocument? _pdf;
    private MediaSource? _media;

    public async Task<PreviewResult> LoadAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await FileActions.ResolveStorageItemAsync(entry, cancellationToken) is not StorageFile file)
        {
            throw new FileNotFoundException();
        }

        string typeName = await Task.Run(() => file.DisplayType, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FileKinds.PreviewOf(entry.Extension, entry.Size) switch
        {
            PreviewKind.Image => await LoadImageAsync(file, typeName, cancellationToken),
            PreviewKind.Svg => await LoadSvgAsync(file, typeName, cancellationToken),
            PreviewKind.Text => await LoadTextAsync(entry, typeName, cancellationToken),
            PreviewKind.Pdf => await LoadPdfAsync(file, typeName, cancellationToken),
            PreviewKind.Media => LoadMedia(file, typeName),
            _ => await LoadThumbnailAsync(entry, typeName, cancellationToken),
        };
    }

    public async Task<object> RenderPageAsync(uint pageIndex, CancellationToken cancellationToken)
    {
        PdfDocument pdf = _pdf ?? throw new InvalidOperationException("No PDF is loaded.");
        cancellationToken.ThrowIfCancellationRequested();
        using PdfPage page = pdf.GetPage(pageIndex);
        PreviewViewport area = viewport();
        double fit = Math.Min(area.Width / page.Size.Width, area.Height / page.Size.Height);
        using InMemoryRandomAccessStream stream = new();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * fit),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * fit),
        }).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapImage image = new();
        await image.SetSourceAsync(stream).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    public void Clear()
    {
        _pdf = null;
        _media?.Dispose();
        _media = null;
    }

    private async Task<PreviewResult> LoadImageAsync(StorageFile file, string typeName, CancellationToken token)
    {
        ImageProperties properties = await Task.Run(async () => await file.Properties.GetImagePropertiesAsync().AsTask(token), token);
        using IRandomAccessStream stream = await OpenAsync(file, token);
        token.ThrowIfCancellationRequested();
        PreviewViewport area = viewport();
        BitmapImage image = new();
        if (properties.Width > 0 && properties.Height > 0)
        {
            double fit = Math.Min(area.Width / (double)properties.Width, area.Height / (double)properties.Height);
            if (fit < 1)
            {
                image.DecodePixelWidth = (int)Math.Max(1, Math.Round(properties.Width * fit));
                image.DecodePixelHeight = (int)Math.Max(1, Math.Round(properties.Height * fit));
            }
        }

        await image.SetSourceAsync(stream).AsTask(token);
        token.ThrowIfCancellationRequested();
        uint width = properties.Width > 0 ? properties.Width : (uint)image.PixelWidth;
        uint height = properties.Height > 0 ? properties.Height : (uint)image.PixelHeight;
        return new PreviewResult(typeName, width > 0 && height > 0 ? $"{width} × {height}" : null, Image: image);
    }

    private async Task<PreviewResult> LoadSvgAsync(StorageFile file, string typeName, CancellationToken token)
    {
        using IRandomAccessStream stream = await OpenAsync(file, token);
        token.ThrowIfCancellationRequested();
        SvgImageSource svg = new();
        if (await svg.SetSourceAsync(stream).AsTask(token) != SvgImageSourceLoadStatus.Success)
        {
            throw new InvalidDataException(text.Get("PreviewFailed"));
        }

        token.ThrowIfCancellationRequested();
        return new PreviewResult(typeName, Image: svg);
    }

    private async Task<PreviewResult> LoadTextAsync(FileEntry entry, string typeName, CancellationToken token)
    {
        TextPreview preview = await TextPreviewReader.ReadAsync(entry.FullPath, token);
        token.ThrowIfCancellationRequested();
        if (preview.Text is null)
        {
            throw new InvalidDataException(text.Get("PreviewFailed"));
        }

        return new PreviewResult(typeName, Text: preview.Truncated
            ? text.Format("TextTruncated", preview.Text, FileKinds.MaxTextPreviewBytes / 1024)
            : preview.Text);
    }

    private async Task<PreviewResult> LoadPdfAsync(StorageFile file, string typeName, CancellationToken token)
    {
        PdfDocument pdf = await Task.Run(async () => await PdfDocument.LoadFromFileAsync(file).AsTask(token), token);
        token.ThrowIfCancellationRequested();
        if (pdf.PageCount == 0)
        {
            throw new InvalidDataException(text.Get("PreviewFailed"));
        }

        _pdf = pdf;
        object firstPage = await RenderPageAsync(0, token);
        return new PreviewResult(typeName, text.Format(pdf.PageCount == 1 ? "PageCount" : "PagesCount", pdf.PageCount), pdf.PageCount, Image: firstPage);
    }

    private PreviewResult LoadMedia(StorageFile file, string typeName)
    {
        _media = MediaSource.CreateFromStorageFile(file);
        return new PreviewResult(typeName, Media: _media);
    }

    /// <summary>No preview for this kind of file: the shell's large icon or thumbnail stands in.</summary>
    private async Task<PreviewResult> LoadThumbnailAsync(FileEntry entry, string typeName, CancellationToken token)
    {
        int size = (int)Math.Ceiling(NoPreviewThumbnailSize * viewport().Scale);
        ShellThumbnail.Pixels? pixels = await Task.Run(() => ShellThumbnail.Load(entry.FullPath, size), token);
        token.ThrowIfCancellationRequested();
        return new PreviewResult(typeName, Thumbnail: pixels is null ? null : ShellThumbnail.ToImage(pixels));
    }

    private static Task<IRandomAccessStreamWithContentType> OpenAsync(StorageFile file, CancellationToken token) =>
        Task.Run(async () => await file.OpenReadAsync().AsTask(token), token);
}
