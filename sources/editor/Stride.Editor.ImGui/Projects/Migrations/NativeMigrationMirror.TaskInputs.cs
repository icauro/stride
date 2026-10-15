using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed partial class NativeMigrationMirror
{
    private readonly Dictionary<string, (OwnedDirectoryIdentity Identity, string[] Entries)> taskInputDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> taskInputRoots = new(StringComparer.OrdinalIgnoreCase);
    private static string[] TaskInputEntries(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Select(path => (Directory.Exists(path) ? "D/" : "F/") + Path.GetFileName(path)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    internal string StageTaskAssembly(string original, CancellationToken token)
    {
        var source = Path.GetFullPath(original); var directory = Path.GetDirectoryName(source)!;
        if (!File.Exists(source)) throw new IOException("The native task assembly is unavailable: " + source);
        if (SameProjectRoot(directory, Path.GetPathRoot(directory)!))
            return StageVolumeTaskPayload(source, token);
        if (!taskInputRoots.TryGetValue(directory, out var targetRoot))
        {
            try { targetRoot = ProjectInputPath(directory); }
            catch (IOException) { targetRoot = Path.Combine(container, "task-inputs", Guid.NewGuid().ToString("N")); }
            for (var parent = directory; parent != null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
            void Copy(string current)
            {
                token.ThrowIfCancellationRequested(); RejectLink(current);
                var identity = WindowsOwnedDirectory.Capture(current); var entries = TaskInputEntries(current);
                if (taskInputDirectories.TryGetValue(current, out var previous) && (previous.Identity != identity || !previous.Entries.SequenceEqual(entries, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException("The native task input directory changed: " + current);
                taskInputDirectories.TryAdd(current, (identity, entries));
                var target = Path.Combine(targetRoot, Path.GetRelativePath(directory, current)); Directory.CreateDirectory(target);
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    token.ThrowIfCancellationRequested(); RejectLink(entry);
                    if (Directory.Exists(entry)) { Copy(entry); continue; }
                    using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var fileIdentity = WindowsOwnedAssetFileWriter.CaptureIdentity(stream);
                    var hash = SHA256.HashData(stream); stream.Position = 0;
                    if (externalProjectFiles.TryGetValue(entry, out var prior) && (prior.Identity != fileIdentity || !hash.AsSpan().SequenceEqual(prior.Hash)))
                        throw new IOException("The native task input file changed: " + entry);
                    var output = Path.Combine(targetRoot, Path.GetRelativePath(directory, entry));
                    if (!File.Exists(output))
                    {
                        using var body = new MemoryStream(); stream.CopyTo(body);
                        WindowsOwnedAssetFileWriter.Write(output, null, body.ToArray());
                    }
                    externalProjectFiles.TryAdd(entry, (hash, fileIdentity));
                }
            }
            Copy(directory); taskInputRoots.Add(directory, targetRoot);
        }
        var assemblyPath = Path.Combine(targetRoot, Path.GetFileName(source));
        using var originalAssembly = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var privateAssembly = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(originalAssembly).AsSpan().SequenceEqual(SHA256.HashData(privateAssembly)))
            throw new IOException("The native task assembly private bytes do not match their source certificate.");
        return assemblyPath;
    }
    private void ValidateTaskInputs()
    {
        foreach (var pair in taskInputDirectories)
            if (WindowsOwnedDirectory.Capture(pair.Key) != pair.Value.Identity || !TaskInputEntries(pair.Key).SequenceEqual(pair.Value.Entries, StringComparer.OrdinalIgnoreCase))
                throw new IOException("A native task deployment changed identity or membership: " + pair.Key);
    }
}
