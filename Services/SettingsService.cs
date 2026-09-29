using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TrayFolder.Core;
using Windows.Storage;

namespace TrayFolder.Services;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(FolderViewOptions))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app. Not for secrets.
/// </summary>
/// <remarks>
/// LocalSettings values must be small (8 KB each) WinRT primitives. Store enums as int, and lists
/// or records as JSON through a source-generated <c>JsonSerializerContext</c> (trim-safe) with
/// <see cref="GetJson"/> / <see cref="SetJson"/>. Anything larger belongs in a file under
/// <c>ApplicationData.Current.LocalFolder</c>.
/// </remarks>
internal static class SettingsService
{
    /// <summary>False until the first launch has shown the flyout.</summary>
    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    /// <summary>The folder the flyout shows; null until the user picks one (Downloads is the default).</summary>
    public static string? FolderPath
    {
        get => Get<string?>(nameof(FolderPath), null);
        set => Set(nameof(FolderPath), value);
    }

    /// <summary>Folders picked before, most recent first, for the header's folder menu.</summary>
    public static List<string> RecentFolders
    {
        get => GetJson(nameof(RecentFolders), SettingsJsonContext.Default.ListString) ?? [];
        set => SetJson(nameof(RecentFolders), value, SettingsJsonContext.Default.ListString);
    }

    public static FolderViewOptions ViewOptions
    {
        get => GetJson(nameof(ViewOptions), SettingsJsonContext.Default.FolderViewOptions) ?? new FolderViewOptions();
        set => SetJson(nameof(ViewOptions), value, SettingsJsonContext.Default.FolderViewOptions);
    }

    public static T? GetJson<T>(string key, JsonTypeInfo<T> typeInfo)
    {
        if (Get<string?>(key, null) is not { } json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static void SetJson<T>(string key, T? value, JsonTypeInfo<T> typeInfo) =>
        Set(key, value is null ? null : JsonSerializer.Serialize(value, typeInfo));

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}
