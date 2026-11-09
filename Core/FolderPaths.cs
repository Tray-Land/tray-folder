namespace TrayFolder.Core;

/// <summary>How the app compares and names folder paths. Windows paths ignore case and a trailing separator.</summary>
public static class FolderPaths
{
    public static bool Same(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder's own name. A drive root has none, so it names itself.</summary>
    public static string LeafName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }
}
