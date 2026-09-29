namespace TrayFolder.Core;

/// <summary>The broad kind of a file, as Explorer's "Kind" column and filter show it.</summary>
public enum FileKind
{
    Folder,
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Program,
    Other,
}

/// <summary>How the flyout's detail page can show a file's contents.</summary>
public enum PreviewKind
{
    None,
    Image,
    Svg,
    Text,
    Media,
    Pdf,
}

/// <summary>Maps file extensions to kinds and preview support. No platform dependencies.</summary>
public static class FileKinds
{
    /// <summary>Text previews read at most this much, so a huge log never stalls the flyout.</summary>
    public const int MaxTextPreviewBytes = 64 * 1024;

    /// <summary>Images larger than this are shown as info only; decoding them costs too much memory.</summary>
    public const long MaxImagePreviewBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> Images = Set(
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".jxr", ".svg");

    private static readonly HashSet<string> Videos = Set(
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".mpg", ".mpeg");

    private static readonly HashSet<string> Audio = Set(
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus");

    private static readonly HashSet<string> Documents = Set(
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
        ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".epub", ".one", ".vsdx");

    private static readonly HashSet<string> Archives = Set(
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso");

    private static readonly HashSet<string> Programs = Set(
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".ps1", ".com", ".lnk");

    private static readonly HashSet<string> TextPreview = Set(
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".log", ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".cs", ".xaml", ".csproj", ".props", ".targets",
        ".sln", ".slnx", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".sh", ".ps1",
        ".psm1", ".bat", ".cmd", ".sql", ".gitignore", ".editorconfig", ".reg", ".srt", ".vtt");

    // Formats WIC decodes out of the box; HEIC/AVIF need the Store codecs, and still usually work.
    private static readonly HashSet<string> ImagePreview = Set(
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".jxr");

    private static readonly HashSet<string> MediaPreview = Set(
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus");

    public static FileKind KindOf(string extension, bool isFolder = false)
    {
        if (isFolder)
        {
            return FileKind.Folder;
        }

        return extension switch
        {
            _ when Images.Contains(extension) => FileKind.Image,
            _ when Videos.Contains(extension) => FileKind.Video,
            _ when Audio.Contains(extension) => FileKind.Audio,
            _ when Documents.Contains(extension) => FileKind.Document,
            _ when Archives.Contains(extension) => FileKind.Archive,
            _ when Programs.Contains(extension) => FileKind.Program,
            _ => FileKind.Other,
        };
    }

    public static PreviewKind PreviewOf(string extension, long size)
    {
        if (ImagePreview.Contains(extension))
        {
            return size <= MaxImagePreviewBytes ? PreviewKind.Image : PreviewKind.None;
        }

        if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Svg;
        }

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Pdf;
        }

        if (MediaPreview.Contains(extension))
        {
            return PreviewKind.Media;
        }

        return TextPreview.Contains(extension) ? PreviewKind.Text : PreviewKind.None;
    }

    /// <summary>Plural label for a kind, as used in group headers and the filter menu.</summary>
    public static string PluralLabel(FileKind kind) => kind switch
    {
        FileKind.Folder => "Folders",
        FileKind.Image => "Pictures",
        FileKind.Video => "Videos",
        FileKind.Audio => "Music",
        FileKind.Document => "Documents",
        FileKind.Archive => "Compressed files",
        FileKind.Program => "Programs and installers",
        _ => "Other",
    };

    /// <summary>Segoe Fluent Icons glyph for a kind, shown until the shell thumbnail arrives.</summary>
    public static string Glyph(FileKind kind) => kind switch
    {
        FileKind.Folder => "",
        FileKind.Image => "",
        FileKind.Video => "",
        FileKind.Audio => "",
        FileKind.Document => "",
        FileKind.Archive => "",
        FileKind.Program => "",
        _ => "",
    };

    private static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);
}
