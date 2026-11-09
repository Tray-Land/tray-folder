using Microsoft.UI.Xaml;
using TrayFolder.Core;

namespace TrayFolder.Views;

/// <summary>Functions for x:Bind. A plain bool needs none of these: x:Bind converts bool to Visibility itself.</summary>
public static class UiBindings
{
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Whether a radio menu item's value is the option's current one.</summary>
    public static bool Is(object current, object value) => Equals(current, value);

    public static bool Contains(List<FileKind> kinds, FileKind kind) => kinds.Contains(kind);
}
