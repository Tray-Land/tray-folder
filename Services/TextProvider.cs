using Microsoft.Windows.ApplicationModel.Resources;
using TrayFolder.Core;

namespace TrayFolder.Services;

internal sealed class TextProvider : ITextProvider
{
    private readonly ResourceLoader _loader = new();

    public string Get(string key)
    {
        string value = _loader.GetString(key);
        return value.Length > 0 ? value : throw new InvalidOperationException($"Missing string resource: {key}");
    }
}
