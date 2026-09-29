using System.Globalization;

namespace TrayFolder.Core;

/// <summary>A labeled run of entries; the whole list is one group with an empty header when ungrouped.</summary>
public sealed record EntryGroup(string Header, IReadOnlyList<FileEntry> Items);

/// <summary>
/// Filters, sorts and groups folder entries the way File Explorer does. Pure: the caller passes
/// "now" and the first day of the week, so tests are deterministic.
/// </summary>
public static class FolderViewPolicy
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;

    public static IReadOnlyList<EntryGroup> Arrange(
        IEnumerable<FileEntry> entries,
        FolderViewOptions options,
        string? search,
        DateTime now,
        DayOfWeek firstDayOfWeek)
    {
        List<FileEntry> visible = entries.Where(e => IsVisible(e, options, search, now)).ToList();
        visible.Sort(Comparer(options));

        if (options.GroupBy == GroupField.None)
        {
            return visible.Count == 0 ? [] : [new EntryGroup(string.Empty, visible)];
        }

        // Group keys carry a rank so groups come out in a natural order (newest first, A before Q),
        // reversed when the user sorts the other way by that same field.
        bool reverse = ReverseGroups(options);
        return visible
            .GroupBy(e => GroupOf(e, options.GroupBy, now, firstDayOfWeek))
            .OrderBy(g => reverse ? -g.Key.Rank : g.Key.Rank)
            .Select(g => new EntryGroup(g.Key.Header, g.ToList()))
            .ToList();
    }

    public static bool IsVisible(FileEntry entry, FolderViewOptions options, string? search, DateTime now)
    {
        if (entry.IsHidden && !options.ShowHidden)
        {
            return false;
        }

        if (entry.IsFolder && !options.ShowFolders)
        {
            return false;
        }

        // A kind filter is about files; folders stay so the user can still navigate.
        if (options.Kinds.Count > 0 && !entry.IsFolder && !options.Kinds.Contains(entry.Kind))
        {
            return false;
        }

        if (options.Date != DateFilter.Any && entry.Modified < DateFilterStart(options.Date, now))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(search)
            || entry.Name.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    public static DateTime DateFilterStart(DateFilter filter, DateTime now) => filter switch
    {
        DateFilter.Today => now.Date,
        DateFilter.LastWeek => now.Date.AddDays(-7),
        DateFilter.LastMonth => now.Date.AddMonths(-1),
        DateFilter.LastYear => now.Date.AddYears(-1),
        _ => DateTime.MinValue,
    };

    /// <summary>Explorer's order: folders first, then the chosen field, then name as a tiebreak.</summary>
    public static Comparison<FileEntry> Comparer(FolderViewOptions options)
    {
        int direction = options.Descending ? -1 : 1;
        return (a, b) =>
        {
            if (a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            int result = options.SortBy switch
            {
                SortField.DateModified => a.Modified.CompareTo(b.Modified),
                SortField.Size => a.Size.CompareTo(b.Size),
                SortField.Type => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };

            if (result == 0)
            {
                result = NaturalCompare(a.Name, b.Name);
                return options.SortBy == SortField.Name ? result * direction : result;
            }

            return result * direction;
        };
    }

    /// <summary>"file2" before "file10", like Explorer (StrCmpLogicalW, approximately).</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                ReadOnlySpan<char> na = a.AsSpan(si, i - si).TrimStart('0');
                ReadOnlySpan<char> nb = b.AsSpan(sj, j - sj).TrimStart('0');
                int byLength = na.Length.CompareTo(nb.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                int byDigits = na.SequenceCompareTo(nb);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0)
            {
                return c;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static (int Rank, string Header) GroupOf(FileEntry entry, GroupField group, DateTime now, DayOfWeek firstDayOfWeek) => group switch
    {
        GroupField.DateModified => DateGroup(entry.Modified, now, firstDayOfWeek),
        GroupField.Size => entry.IsFolder ? (-1, "Folders") : SizeGroup(entry.Size),
        GroupField.Kind => ((int)entry.Kind, FileKinds.PluralLabel(entry.Kind)),
        GroupField.Name => NameGroup(entry.Name),
        _ => (0, string.Empty),
    };

    /// <summary>Explorer's relative date groups, newest first (lower rank).</summary>
    public static (int Rank, string Header) DateGroup(DateTime modified, DateTime now, DayOfWeek firstDayOfWeek)
    {
        DateTime today = now.Date;
        DateTime startOfWeek = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        DateTime startOfMonth = new(today.Year, today.Month, 1);
        DateTime startOfYear = new(today.Year, 1, 1);

        return modified switch
        {
            _ when modified >= today => (0, "Today"),
            _ when modified >= today.AddDays(-1) => (1, "Yesterday"),
            _ when modified >= startOfWeek => (2, "Earlier this week"),
            _ when modified >= startOfWeek.AddDays(-7) => (3, "Last week"),
            _ when modified >= startOfMonth => (4, "Earlier this month"),
            _ when modified >= startOfMonth.AddMonths(-1) => (5, "Last month"),
            _ when modified >= startOfYear => (6, "Earlier this year"),
            _ => (7, "A long time ago"),
        };
    }

    /// <summary>Explorer's size buckets, smallest first.</summary>
    public static (int Rank, string Header) SizeGroup(long size) => size switch
    {
        0 => (0, "Empty (0 KB)"),
        < 16 * KB => (1, "Tiny (0 - 16 KB)"),
        < MB => (2, "Small (16 KB - 1 MB)"),
        < 128 * MB => (3, "Medium (1 - 128 MB)"),
        < GB => (4, "Large (128 MB - 1 GB)"),
        < 4 * GB => (5, "Huge (1 - 4 GB)"),
        _ => (6, "Gigantic (> 4 GB)"),
    };

    /// <summary>Explorer's name ranges.</summary>
    public static (int Rank, string Header) NameGroup(string name)
    {
        char first = name.Length == 0 ? ' ' : char.ToUpperInvariant(name[0]);
        return first switch
        {
            >= '0' and <= '9' => (0, "0 - 9"),
            >= 'A' and <= 'H' => (1, "A - H"),
            >= 'I' and <= 'P' => (2, "I - P"),
            >= 'Q' and <= 'Z' => (3, "Q - Z"),
            _ => (4, "Other"),
        };
    }

    /// <summary>Explorer-style size text: "0 bytes", "12 KB", "3.4 MB".</summary>
    public static string FormatSize(long size, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return size switch
        {
            < KB => string.Format(culture, "{0:N0} bytes", size),
            < MB => string.Format(culture, "{0:0.#} KB", size / (double)KB),
            < GB => string.Format(culture, "{0:0.#} MB", size / (double)MB),
            _ => string.Format(culture, "{0:0.##} GB", size / (double)GB),
        };
    }

    private static bool ReverseGroups(FolderViewOptions options) => (options.SortBy, options.GroupBy) switch
    {
        // Date groups already run newest first; sorting oldest-first flips them.
        (SortField.DateModified, GroupField.DateModified) => !options.Descending,
        (SortField.Name, GroupField.Name) or (SortField.Size, GroupField.Size) or (SortField.Type, GroupField.Kind) => options.Descending,
        _ => false,
    };
}
