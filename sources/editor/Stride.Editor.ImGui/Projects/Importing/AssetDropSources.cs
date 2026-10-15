namespace Stride.Editor.ImGui;

/// <summary>Expands native file/directory drops without touching editor graphs.</summary>
internal static class AssetDropSources
{
    internal static string[] Expand(string[] inputs, CancellationToken cancellation)
    {
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(inputs.Reverse());
        while (pending.TryPop(out var path))
        {
            cancellation.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path))
            {
                if (seenFiles.Add(path)) files.Add(path);
                continue;
            }
            var directory = new DirectoryInfo(path);
            var identity = (directory.Attributes & FileAttributes.ReparsePoint) != 0
                ? directory.ResolveLinkTarget(true)?.FullName ?? path : path;
            if (!directories.Add(Path.GetFullPath(identity))) continue;
            // Enumerate lazily so cancellation is observed during large traversals.
            foreach (var file in Directory.EnumerateFiles(path))
            {
                cancellation.ThrowIfCancellationRequested();
                if (seenFiles.Add(file)) files.Add(file);
            }
            foreach (var child in Directory.EnumerateDirectories(path))
            { cancellation.ThrowIfCancellationRequested(); pending.Push(child); }
        }
        return files.ToArray();
    }
}
