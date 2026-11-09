using System.Security.Principal;
using TrayFolder.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace TrayFolder.Services;

/// <summary>
/// Deletes to the Recycle Bin through the shell (so it prompts and reports errors like Explorer),
/// then finds each item's <c>$I</c> record in the bin so the delete can be undone. Restoring is what
/// the shell does: move the <c>$R</c> payload back to the original path and drop the record.
/// </summary>
/// <param name="ownerWindow">The flyout's window, so the shell's dialogs sit on top of it. Read on the UI thread.</param>
internal sealed class RecycleBinService(Func<nint> ownerWindow) : IFileDeleter
{
    private const string BinFolder = "$Recycle.Bin";
    private const string RecordPrefix = "$I";
    private const string PayloadPrefix = "$R";
    private const int ErrorCancelled = 0x4C7;

    // FOF_ALLOWUNDO sends to the Recycle Bin; FOF_WANTNUKEWARNING makes the shell ask before it
    // deletes for good something that can't be recycled (too big, or on a drive with no bin).
    private const ushort AllowUndo = 0x0040;
    private const ushort WantNukeWarning = 0x4000;
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(5);

    public Task<DeleteOutcome> DeleteAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        nint owner = ownerWindow();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime started = DateTime.UtcNow - ClockSlack;
            int result = ShellDelete(owner, entries);

            List<FileEntry> gone = [.. entries.Where(e => !Exists(e))];
            Dictionary<string, List<RecycleRecordFile>> bins = new(StringComparer.OrdinalIgnoreCase);
            List<DeletedItem> recycled = [];
            foreach (FileEntry entry in gone)
            {
                string root = Path.GetPathRoot(entry.FullPath) ?? string.Empty;
                if (!bins.TryGetValue(root, out List<RecycleRecordFile>? records))
                {
                    bins[root] = records = ReadBin(root, started);
                }

                RecycleRecordFile? match = records
                    .Where(r => string.Equals(r.Record.OriginalPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Record.DeletedUtc)
                    .FirstOrDefault();
                if (match is not null)
                {
                    recycled.Add(new DeletedItem(entry, match.Path));
                    records.Remove(match);
                }
            }

            int remaining = entries.Count - gone.Count;

            // The user cancelling the shell's prompt is not a failure; anything else with nothing deleted is.
            if (result != 0 && result != ErrorCancelled && gone.Count == 0)
            {
                throw new IOException($"Windows could not delete the selected items (error 0x{result:X}).");
            }

            return new DeleteOutcome(recycled, gone.Count - recycled.Count, remaining);
        }, cancellationToken);
    }

    public Task<int> RestoreAsync(IReadOnlyList<DeletedItem> items, CancellationToken cancellationToken) => Task.Run(() =>
    {
        int restored = 0;
        foreach (DeletedItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(item.RecordPath) ?? string.Empty;
            string payload = Path.Combine(directory, PayloadPrefix + Path.GetFileName(item.RecordPath)[RecordPrefix.Length..]);
            string target = item.Entry.FullPath;

            // Never overwrite something that has taken the name since.
            if (!(File.Exists(payload) || Directory.Exists(payload)) || File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (item.Entry.IsFolder)
            {
                Directory.Move(payload, target);
            }
            else
            {
                File.Move(payload, target);
            }

            File.Delete(item.RecordPath);
            restored++;
        }

        return restored;
    }, cancellationToken);

    private static bool Exists(FileEntry entry) => entry.IsFolder ? Directory.Exists(entry.FullPath) : File.Exists(entry.FullPath);

    /// <summary>This user's records in a drive's bin that are newer than <paramref name="since"/>.</summary>
    private static List<RecycleRecordFile> ReadBin(string root, DateTime since)
    {
        List<RecycleRecordFile> records = [];
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            string folder = Path.Combine(root, BinFolder, sid ?? string.Empty);
            if (sid is null || !Directory.Exists(folder))
            {
                return records;
            }

            EnumerationOptions options = new() { AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (string file in Directory.EnumerateFiles(folder, RecordPrefix + "*", options))
            {
                if (RecycleRecord.Parse(File.ReadAllBytes(file)) is { } record && record.DeletedUtc >= since)
                {
                    records.Add(new RecycleRecordFile(file, record));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No readable bin means no undo, not a failed delete.
        }

        return records;
    }

    private static unsafe int ShellDelete(nint owner, IReadOnlyList<FileEntry> entries)
    {
        // pFrom is a list of paths, each ending in a NUL, with one more NUL at the end.
        char[] from = [.. string.Concat(entries.Select(e => e.FullPath + '\0')), '\0'];
        fixed (char* paths = from)
        {
            SHFILEOPSTRUCTW operation = new()
            {
                hwnd = new HWND(owner),
                wFunc = PInvoke.FO_DELETE,
                pFrom = paths,
                fFlags = AllowUndo | WantNukeWarning,
            };
            return PInvoke.SHFileOperation(ref operation);
        }
    }

    private sealed record RecycleRecordFile(string Path, RecycleRecord Record);
}
