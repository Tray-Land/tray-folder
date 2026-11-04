using System.Globalization;

namespace TrayFolder.Core;

public static class FileDateFormatter
{
    public static string Format(DateTime date, DateTime now, CultureInfo culture, ITextProvider text)
    {
        string time = date.ToString("t", culture);
        return date.Date == now.Date ? text.Format("TodayTime", time)
            : date.Date == now.Date.AddDays(-1) ? text.Format("YesterdayTime", time)
            : date.Year == now.Year ? $"{date.ToString("MMM d", culture)} {time}"
            : date.ToString("d", culture);
    }
}
