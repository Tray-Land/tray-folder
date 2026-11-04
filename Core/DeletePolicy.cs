namespace TrayFolder.Core;

/// <summary>Why a delete needs the user's explicit go-ahead before anything is touched.</summary>
[Flags]
public enum DeleteConcern
{
    None = 0,
    ManyItems = 1,
    Folders = 2,
    Programs = 4,
    SystemFiles = 8,
    ReadOnlyFiles = 16,
    LargeSize = 32,
}

/// <summary>What a delete would touch, and whether it must be confirmed first.</summary>
public sealed record DeleteAssessment(DeleteConcern Concerns, int Count, int Folders, int Programs, int SystemFiles, int ReadOnlyFiles, long TotalSize)
{
    public bool RequiresConfirmation => Concerns != DeleteConcern.None;
}

/// <summary>
/// The classic delete warnings: a lot of files, folders (which hold more than they show), programs,
/// system files, read-only files, or a lot of data. Pure, so tests can pin the thresholds.
/// </summary>
public static class DeletePolicy
{
    /// <summary>This many items or more is "a large number".</summary>
    public const int LargeCount = 10;

    /// <summary>This many bytes or more is worth a second look, whatever the count.</summary>
    public const long LargeBytes = 1024L * 1024 * 1024;

    public static DeleteAssessment Assess(IReadOnlyCollection<FileEntry> entries)
    {
        int folders = entries.Count(e => e.IsFolder);
        int programs = entries.Count(e => !e.IsFolder && e.Kind == FileKind.Program &&
            !string.Equals(e.Extension, ".lnk", StringComparison.OrdinalIgnoreCase));
        int system = entries.Count(e => e.IsSystem);
        int readOnly = entries.Count(e => e.IsReadOnly);
        long size = entries.Sum(e => e.Size);

        DeleteConcern concerns = DeleteConcern.None;
        Add(entries.Count >= LargeCount, DeleteConcern.ManyItems);
        Add(folders > 0, DeleteConcern.Folders);
        Add(programs > 0, DeleteConcern.Programs);
        Add(system > 0, DeleteConcern.SystemFiles);
        Add(readOnly > 0, DeleteConcern.ReadOnlyFiles);
        Add(size >= LargeBytes, DeleteConcern.LargeSize);
        return new DeleteAssessment(concerns, entries.Count, folders, programs, system, readOnly, size);

        void Add(bool condition, DeleteConcern concern)
        {
            if (condition)
            {
                concerns |= concern;
            }
        }
    }
}

/// <summary>One item in the Recycle Bin: where it came from, and when it went in.</summary>
public sealed record RecycleRecord(string OriginalPath, long Size, DateTime DeletedUtc)
{
    private const long Version1 = 1;
    private const long Version2 = 2;
    private const int Version1PathOffset = 24;

    /// <summary>
    /// Reads a <c>$I</c> metadata file from a drive's <c>$Recycle.Bin</c>. Version 1 (before Windows 10)
    /// has a fixed 520-byte path; version 2 has a length-prefixed one. Null if the bytes are neither.
    /// </summary>
    public static RecycleRecord? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Version1PathOffset)
        {
            return null;
        }

        long version = BitConverter.ToInt64(data);
        long size = BitConverter.ToInt64(data[8..]);
        long fileTime = BitConverter.ToInt64(data[16..]);
        string path;
        if (version == Version1)
        {
            path = Decode(data[Version1PathOffset..]);
        }
        else if (version == Version2 && data.Length >= Version1PathOffset + 4)
        {
            int chars = BitConverter.ToInt32(data[Version1PathOffset..]);
            ReadOnlySpan<byte> rest = data[(Version1PathOffset + 4)..];
            if (chars <= 0 || chars * 2L > rest.Length)
            {
                return null;
            }

            path = Decode(rest[..(chars * 2)]);
        }
        else
        {
            return null;
        }

        if (path.Length == 0 || fileTime is < 0 or > 2650467743999999999L)
        {
            return null;
        }

        return new RecycleRecord(path, size, DateTime.FromFileTimeUtc(fileTime));
    }

    private static string Decode(ReadOnlySpan<byte> utf16)
    {
        string text = System.Text.Encoding.Unicode.GetString(utf16);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }
}
