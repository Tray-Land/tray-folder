using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using TrayFolder.Core;
using TrayFolder.Models;
using TrayFolder.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace TrayFolder.Views;

/// <summary>
/// One file, inside the flyout: a preview when Windows can render it (pictures, SVG, text, PDF,
/// audio and video), otherwise its shell icon, plus its details and actions. The preview drags
/// out like a row in the list. Everything it loads is released when the page goes back.
/// </summary>
public sealed partial class DetailPage : Page, IDisposable
{
    // Used before the first layout pass, when the preview area has no size yet.
    private const double FallbackPreviewWidth = 376;
    private const double FallbackPreviewHeight = 340;

    private readonly TrayFlyoutWindow _host;
    private IReadOnlyList<FileItem> _siblings = [];
    private FileItem? _item;
    private int _generation;
    private PdfDocument? _pdf;
    private uint _pdfPage;
    private MediaSource? _media;

    public DetailPage(TrayFlyoutWindow host)
    {
        InitializeComponent();
        _host = host;
    }

    public void Show(FileItem item, IReadOnlyList<FileItem> siblings)
    {
        _siblings = siblings;
        _ = LoadAsync(item);
    }

    public void OnShown() => BackButton.Focus(FocusState.Programmatic);

    /// <summary>The flyout hid: don't keep playing into an invisible window.</summary>
    public void OnHidden() => PreviewMedia.MediaPlayer?.Pause();

    public void Dispose()
    {
        _generation++;
        ClearPreview();
    }

    private async Task LoadAsync(FileItem item)
    {
        int generation = ++_generation;
        ClearPreview();
        _item = item;

        NameText.Text = item.Entry.Name;
        ToolTipService.SetToolTip(NameText, item.Entry.Name);
        HandleGlyph.Glyph = item.Glyph;
        HandleThumbnail.Source = item.Thumbnail;
        HandleGlyph.Visibility = item.GlyphVisibility;
        int index = IndexOf(item);
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index >= 0 && index < _siblings.Count - 1;
        CopyText.Text = "Copy";

        ShowInfo(item, null);
        PreviewLoading.IsActive = true;

        PreviewKind kind = FileKinds.PreviewOf(item.Entry.Extension, item.Entry.Size);
        // Text selection and the media slider need the pointer; those drag from the header icon instead.
        PreviewArea.CanDrag = kind is not (PreviewKind.Text or PreviewKind.Media);
        ToolTipService.SetToolTip(PreviewArea, PreviewArea.CanDrag ? "Drag to copy this file" : null);
        try
        {
            if (await item.GetStorageItemAsync() is not StorageFile file)
            {
                throw new FileNotFoundException();
            }

            if (generation != _generation)
            {
                return;
            }

            string? dimensions = kind switch
            {
                PreviewKind.Image => await ShowImageAsync(file, generation),
                PreviewKind.Svg => await ShowSvgAsync(file),
                PreviewKind.Text => await ShowTextAsync(file.Path, generation),
                PreviewKind.Pdf => await ShowPdfAsync(file, generation),
                PreviewKind.Media => ShowMedia(file, item),
                _ => null,
            };

            if (generation != _generation)
            {
                return;
            }

            if (kind == PreviewKind.None || dimensions == Unpreviewable)
            {
                await ShowIconAsync(file, generation, kind == PreviewKind.None ? "No preview available" : "Can't preview this file");
                dimensions = null;
            }

            ShowInfo(item, dimensions);
        }
        catch (Exception) when (generation == _generation)
        {
            ClearPreview();
            ShowIconFallback(item, File.Exists(item.FullPath) ? "Can't preview this file" : "This file was moved or deleted");
        }
        finally
        {
            if (generation == _generation)
            {
                PreviewLoading.IsActive = false;
            }
        }
    }

    /// <summary>Returned by a previewer that found the file can't be shown after all.</summary>
    private const string Unpreviewable = "\0";

    private async Task<string?> ShowImageAsync(StorageFile file, int generation)
    {
        ImageProperties properties = await file.Properties.GetImagePropertiesAsync();
        using IRandomAccessStream stream = await file.OpenReadAsync();

        // Decode at the size it's shown, not the camera's 50 MP: memory stays flat in the flyout.
        (int width, int height) = PreviewPixelSize();
        BitmapImage image = new();
        if (properties.Width > 0 && properties.Height > 0)
        {
            double fit = Math.Min(width / (double)properties.Width, height / (double)properties.Height);
            if (fit < 1)
            {
                image.DecodePixelWidth = (int)Math.Round(properties.Width * fit);
                image.DecodePixelHeight = (int)Math.Round(properties.Height * fit);
            }
        }

        await image.SetSourceAsync(stream);
        if (generation != _generation)
        {
            return null;
        }

        PreviewImage.Source = image;
        PreviewImage.Visibility = Visibility.Visible;
        uint w = properties.Width > 0 ? properties.Width : (uint)image.PixelWidth;
        uint h = properties.Height > 0 ? properties.Height : (uint)image.PixelHeight;
        return w > 0 && h > 0 ? $"{w} × {h}" : null;
    }

    private async Task<string?> ShowSvgAsync(StorageFile file)
    {
        using IRandomAccessStream stream = await file.OpenReadAsync();
        SvgImageSource svg = new();
        SvgImageSourceLoadStatus status = await svg.SetSourceAsync(stream);
        if (status != SvgImageSourceLoadStatus.Success)
        {
            return Unpreviewable;
        }

        PreviewImage.Source = svg;
        PreviewImage.Visibility = Visibility.Visible;
        return null;
    }

    private async Task<string?> ShowTextAsync(string path, int generation)
    {
        (string? text, bool truncated) = await Task.Run(() => ReadTextPreview(path));
        if (text is null)
        {
            return Unpreviewable;
        }

        if (generation == _generation)
        {
            PreviewText.Text = truncated ? $"{text}\n\n… (showing the first {FileKinds.MaxTextPreviewBytes / 1024} KB)" : text;
            PreviewTextScroller.Visibility = Visibility.Visible;
        }

        return null;
    }

    /// <summary>Reads the start of a text file; null when it turns out to be binary.</summary>
    private static (string? Text, bool Truncated) ReadTextPreview(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[FileKinds.MaxTextPreviewBytes];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        // A NUL in the first few KB means binary; UTF-16 text is handled by the BOM check first.
        bool utf16 = read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!utf16 && Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8192)) >= 0)
        {
            return (null, false);
        }

        using StreamReader reader = new(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (reader.ReadToEnd(), stream.Length > read);
    }

    private async Task<string?> ShowPdfAsync(StorageFile file, int generation)
    {
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
        if (generation != _generation || pdf.PageCount == 0)
        {
            return Unpreviewable;
        }

        _pdf = pdf;
        _pdfPage = 0;
        await RenderPdfPageAsync(generation);
        PdfPager.Visibility = pdf.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        return pdf.PageCount == 1 ? "1 page" : $"{pdf.PageCount} pages";
    }

    private async Task RenderPdfPageAsync(int generation)
    {
        if (_pdf is null)
        {
            return;
        }

        (int width, int height) = PreviewPixelSize();
        using PdfPage page = _pdf.GetPage(_pdfPage);
        double fit = Math.Min(width / page.Size.Width, height / page.Size.Height);
        using InMemoryRandomAccessStream stream = new();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * fit),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * fit),
        });

        BitmapImage image = new();
        await image.SetSourceAsync(stream);
        if (generation != _generation)
        {
            return;
        }

        PreviewImage.Source = image;
        PreviewImage.Visibility = Visibility.Visible;
        PdfPageText.Text = $"{_pdfPage + 1} / {_pdf.PageCount}";
    }

    private string? ShowMedia(StorageFile file, FileItem item)
    {
        _media = MediaSource.CreateFromStorageFile(file);
        PreviewMedia.PosterSource = item.Entry.Kind == FileKind.Audio ? item.Thumbnail : null;
        PreviewMedia.Source = _media;
        PreviewMedia.Visibility = Visibility.Visible;
        return null;
    }

    private async Task ShowIconAsync(StorageFile file, int generation, string message)
    {
        ShowIconFallback(_item!, message);
        string path = file.Path;
        int size = (int)Math.Ceiling(128 * (XamlRoot?.RasterizationScale ?? 1));
        ShellThumbnail.Pixels? pixels = await Task.Run(() => ShellThumbnail.Load(path, size));
        if (pixels is not null && generation == _generation)
        {
            NoPreviewThumbnail.Source = ShellThumbnail.ToImage(pixels);
            NoPreviewGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowIconFallback(FileItem item, string message)
    {
        NoPreviewGlyph.Glyph = item.Glyph;
        NoPreviewGlyph.Visibility = Visibility.Visible;
        NoPreviewText.Text = message;
        NoPreview.Visibility = Visibility.Visible;
        PreviewArea.CanDrag = true;
        ToolTipService.SetToolTip(PreviewArea, "Drag to copy this file");
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Text = string.Empty;
        PreviewTextScroller.Visibility = Visibility.Collapsed;
        NoPreview.Visibility = Visibility.Collapsed;
        NoPreviewThumbnail.Source = null;
        PdfPager.Visibility = Visibility.Collapsed;
        _pdf = null;

        PreviewMedia.MediaPlayer?.Pause();
        PreviewMedia.Source = null;
        PreviewMedia.PosterSource = null;
        PreviewMedia.Visibility = Visibility.Collapsed;
        _media?.Dispose();
        _media = null;
    }

    private (int Width, int Height) PreviewPixelSize()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double width = PreviewArea.ActualWidth > 0 ? PreviewArea.ActualWidth - 16 : FallbackPreviewWidth;
        double height = PreviewArea.ActualHeight > 0 ? PreviewArea.ActualHeight - 16 : FallbackPreviewHeight;
        return ((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
    }

    private void ShowInfo(FileItem item, string? dimensions)
    {
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        CultureInfo culture = CultureInfo.CurrentCulture;

        AddInfo("Type", item.TypeName);
        if (dimensions is not null)
        {
            AddInfo(item.Entry.Kind == FileKind.Document ? "Pages" : "Dimensions", dimensions);
        }

        AddInfo("Size", item.Entry.Size < 1024 ? item.SizeText : $"{item.SizeText} ({item.Entry.Size.ToString("N0", culture)} bytes)");
        AddInfo("Modified", item.Entry.Modified.ToString("f", culture));
        AddInfo("Created", item.Entry.Created.ToString("f", culture));
        AddInfo("Location", Path.GetDirectoryName(item.FullPath) ?? item.FullPath, wrap: true);
    }

    private void AddInfo(string label, string value, bool wrap = false)
    {
        int row = InfoGrid.RowDefinitions.Count;
        InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock labelText = new()
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        labelText.SetValue(Grid.RowProperty, row);
        labelText.Opacity = 0.7;

        TextBlock valueText = new()
        {
            Text = value,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            IsTextSelectionEnabled = true,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = wrap ? 2 : 1,
        };
        valueText.SetValue(Grid.RowProperty, row);
        valueText.SetValue(Grid.ColumnProperty, 1);
        ToolTipService.SetToolTip(valueText, value);

        InfoGrid.Children.Add(labelText);
        InfoGrid.Children.Add(valueText);
    }

    private int IndexOf(FileItem item)
    {
        for (int i = 0; i < _siblings.Count; i++)
        {
            if (ReferenceEquals(_siblings[i], item) || string.Equals(_siblings[i].FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void Step(int delta)
    {
        if (_item is null)
        {
            return;
        }

        int index = IndexOf(_item) + delta;
        if (index >= 0 && index < _siblings.Count)
        {
            _ = LoadAsync(_siblings[index]);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.ShowMainPage();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Previous_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Leave arrow keys to text selection, the media slider and other focused controls.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Slider)
        {
            return;
        }

        Step(-1);
        args.Handled = true;
    }

    private void Next_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Slider)
        {
            return;
        }

        Step(1);
        args.Handled = true;
    }

    private async void PdfPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_pdf is not null && _pdfPage > 0)
        {
            _pdfPage--;
            await RenderPdfPageAsync(_generation);
        }
    }

    private async void PdfNext_Click(object sender, RoutedEventArgs e)
    {
        if (_pdf is not null && _pdfPage + 1 < _pdf.PageCount)
        {
            _pdfPage++;
            await RenderPdfPageAsync(_generation);
        }
    }

    private void PreviewArea_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (_item is null)
        {
            args.Cancel = true;
            return;
        }

        FileActions.FillDragData(args.Data, [_item]);
        args.AllowedOperations = DataPackageOperation.Copy;
        args.DragUI.SetContentFromDataPackage();
        _host.SetDragging(true);
    }

    private void PreviewArea_DropCompleted(UIElement sender, DropCompletedEventArgs args) => _host.SetDragging(false);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            PreviewMedia.MediaPlayer?.Pause();
            FileActions.Open(_item.FullPath);
        }
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            FileActions.OpenWith(_item.FullPath);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private async void Copy_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Ctrl+C inside the text preview copies the selected text instead.
        if (PreviewText.SelectedText.Length > 0)
        {
            return;
        }

        args.Handled = true;
        await CopyAsync();
    }

    private async Task CopyAsync()
    {
        if (_item is not null && await FileActions.CopyToClipboardAsync([_item]))
        {
            CopyText.Text = "Copied";
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            FileActions.CopyPaths([_item]);
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            FileActions.ShowInExplorer(_item.FullPath);
        }
    }
}
