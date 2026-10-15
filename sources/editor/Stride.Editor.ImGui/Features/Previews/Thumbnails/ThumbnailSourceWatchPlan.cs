using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Tracks exact source paths while watching an existing ancestor of a missing directory.</summary>
internal sealed class ThumbnailSourceWatchPlan
{
    private readonly Dictionary<string, List<AssetId>> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> directories = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, List<AssetId>> Files => files;
    internal IReadOnlyDictionary<string, bool> Directories => directories;

    internal void Add(string sourcePath, AssetId asset)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!files.TryGetValue(path, out var ids)) files[path] = ids = [];
        if (!ids.Contains(asset)) ids.Add(asset);
        var parent = Path.GetDirectoryName(path);
        var recursive = false;
        while (parent != null && !Directory.Exists(parent))
        { recursive = true; parent = Path.GetDirectoryName(parent); }
        if (parent != null)
        {
            directories[parent] = directories.GetValueOrDefault(parent) || recursive;
            // A watcher rooted in the leaf itself cannot reliably report its
            // own rename/deletion. Its surviving parent supplies those events.
            if (Path.GetDirectoryName(parent) is { } ancestor && Directory.Exists(ancestor))
                directories.TryAdd(ancestor, false);
        }
    }

    internal IEnumerable<AssetId> Affected(string eventPath)
    {
        var path = Path.GetFullPath(eventPath);
        if (files.TryGetValue(path, out var exact)) return exact;
        // Folder creation/deletion/rename invalidates real tracked descendants,
        // including files that still do not exist below a newly created directory.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var ids = new HashSet<AssetId>();
        foreach (var file in files)
            if (file.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ids.UnionWith(file.Value);
        return ids;
    }
}
