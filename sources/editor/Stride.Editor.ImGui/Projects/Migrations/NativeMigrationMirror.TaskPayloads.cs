using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class NativeMigrationMirror
{
    private string StageVolumeTaskPayload(string assembly, CancellationToken token)
    {
        var sourceRoot = Path.GetDirectoryName(assembly)!;
        RejectLink(sourceRoot);
        var identity = WindowsOwnedDirectory.Capture(sourceRoot);
        if (boundedImportDirectories.TryGetValue(sourceRoot, out var previous) && previous != identity)
            throw new IOException("The native task payload root changed identity: " + sourceRoot);
        boundedImportDirectories.TryAdd(sourceRoot, identity);
        if (!taskInputRoots.TryGetValue(sourceRoot, out var targetRoot))
        {
            targetRoot = Path.Combine(container, "task-inputs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(targetRoot); taskInputRoots.Add(sourceRoot, targetRoot);
        }
        var pending = new Queue<string>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Observe(string path)
        {
            var exists = File.Exists(path);
            if (ancestorPolicyPresence.TryGetValue(path, out var prior) && prior != exists)
                throw new IOException("A declared task payload changed presence: " + path);
            ancestorPolicyPresence.TryAdd(path, exists); return exists;
        }
        void Include(string path)
        {
            var full = Path.GetFullPath(path);
            if (!Inside(sourceRoot, full)) throw new IOException("A task payload reference requires another real input ownership root: " + full);
            if (Observe(full)) pending.Enqueue(full);
        }
        Include(assembly);
        while (pending.TryDequeue(out var source))
        {
            token.ThrowIfCancellationRequested(); if (!visited.Add(source)) continue;
            RejectLink(source);
            for (var parent = Path.GetDirectoryName(source); parent != null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
            var parentPath = Path.GetDirectoryName(source)!;
            var parentIdentity = WindowsOwnedDirectory.Capture(parentPath);
            if (boundedImportDirectories.TryGetValue(parentPath, out var previousParent) && previousParent != parentIdentity)
                throw new IOException("A declared task payload parent changed identity: " + parentPath);
            boundedImportDirectories.TryAdd(parentPath, parentIdentity);
            byte[] body; OwnedFileIdentity fileIdentity;
            using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileIdentity = WindowsOwnedAssetFileWriter.CaptureIdentity(stream);
                using var bytes = new MemoryStream(); stream.CopyTo(bytes); body = bytes.ToArray();
            }
            var hash = SHA256.HashData(body);
            if (externalProjectFiles.TryGetValue(source, out var old) && (old.Identity != fileIdentity || !hash.AsSpan().SequenceEqual(old.Hash)))
                throw new IOException("A declared task payload changed during preparation: " + source);
            externalProjectFiles.TryAdd(source, (hash, fileIdentity));
            var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (!SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(hash))
                    throw new IOException("The private declared task payload does not match its native source certificate: " + source);
            }
            else WindowsOwnedAssetFileWriter.Write(target, null, body);
            if (Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var image = new MemoryStream(body); using var pe = new PEReader(image);
                if (pe.HasMetadata)
                {
                    var metadata = pe.GetMetadataReader();
                    foreach (var handle in metadata.AssemblyReferences)
                    {
                        var reference = metadata.GetAssemblyReference(handle);
                        var name = metadata.GetString(reference.Name); var culture = metadata.GetString(reference.Culture);
                        if (Path.GetFileName(name) != name || (!string.IsNullOrEmpty(culture) && Path.GetFileName(culture) != culture))
                            throw new IOException("The native assembly identity is not an adjacent payload filename.");
                        var directory = Path.GetDirectoryName(source)!;
                        Include(Path.Combine(directory, name + ".dll"));
                        if (culture.Length > 0) Include(Path.Combine(directory, culture, name + ".dll"));
                    }
                }
                var stem = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source));
                foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json", ".config", ".pdb" }) Include(stem + suffix);
                Include(source + ".config");
            }
            if (source.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            {
                using var manifest = JsonDocument.Parse(body);
                if (!manifest.RootElement.TryGetProperty("targets", out var targets)) continue;
                foreach (var framework in targets.EnumerateObject())
                foreach (var library in framework.Value.EnumerateObject())
                foreach (var category in new[] { "runtime", "runtimeTargets", "native", "resources" })
                {
                    if (!library.Value.TryGetProperty(category, out var entries)) continue;
                    foreach (var entry in entries.EnumerateObject())
                    {
                        if (Path.IsPathRooted(entry.Name)) throw new IOException("An absolute task manifest payload requires a real cross-root resolver adapter.");
                        var declared = Path.GetFullPath(entry.Name.Replace('/', Path.DirectorySeparatorChar), Path.GetDirectoryName(source)!);
                        if (!Inside(sourceRoot, declared)) throw new IOException("The declared task payload leaves its bounded root.");
                        Include(declared);
                        // Native deployed managed assemblies can be flattened by the
                        // SDK; retain the actual adjacent file, never invent a file at
                        // the manifest's package-cache spelling.
                        if (!File.Exists(declared)) Include(Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(declared)));
                    }
                }
            }
        }
        return Path.Combine(targetRoot, Path.GetFileName(assembly));
    }
}
