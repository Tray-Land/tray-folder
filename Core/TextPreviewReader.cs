using System.Text;

namespace TrayFolder.Core;

public sealed record TextPreview(string? Text, bool Truncated);

public static class TextPreviewReader
{
    public static async Task<TextPreview> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: true);
        byte[] buffer = new byte[FileKinds.MaxTextPreviewBytes];
        int read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        bool utf16 = read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!utf16 && Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8192)) >= 0)
        {
            return new TextPreview(null, false);
        }

        using StreamReader reader = new(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(cancellationToken);
        return new TextPreview(text, stream.Length > read);
    }
}
