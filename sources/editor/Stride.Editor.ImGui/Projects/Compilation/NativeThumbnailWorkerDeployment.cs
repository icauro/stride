using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

/// <summary>Private runnable worker: certified canonical modules are present before CLR initialization.</summary>
internal static class NativeThumbnailWorkerDeployment
{
    internal static string Create(string destination, NativeThumbnailCompilationSnapshot snapshot, CancellationToken token)
    {
        var original = AssetCompilerLocator.GetExecutable();
        var source = Path.GetDirectoryName(original)!;
        // Validate the complete closure before writing or launching a generation.
        // One default load context cannot host different certified images sharing
        // a simple name; report every conflict rather than failing one root later.
        var conflicts = snapshot.Assemblies.GroupBy(item => new AssemblyName(item.Certificate.Identity).Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Certificate.Identity + "|" + item.Certificate.ModuleVersionId + "|" + item.Certificate.Sha256)
                .Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(group => string.Join("; ", group.Select(item => item.Certificate.Identity + " [" + item.Certificate.ModuleVersionId + "]"))).ToArray();
        if (conflicts.Length != 0)
            throw new InvalidDataException("The certified thumbnail closure contains incompatible same-name modules: " + string.Join(" | ", conflicts));
        foreach (var assembly in snapshot.Assemblies)
        {
            token.ThrowIfCancellationRequested();
            assembly.Artifact.Validate(assembly.Certificate);
        }
        Directory.CreateDirectory(destination);
        CopyDirectory(source, destination);
        var shaderRoot = Path.GetFullPath(Path.Combine(destination, "Shaders", "Engine"));
        foreach (var shader in snapshot.ShaderSources ?? [])
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(shader.RelativePath, shaderRoot);
            if (!target.StartsWith(shaderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Convert.ToHexString(SHA256.HashData(shader.Bytes)) != shader.Sha256)
                throw new InvalidDataException("Invalid certified native thumbnail shader source: " + shader.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, shader.Bytes);
        }
        foreach (var assembly in snapshot.Assemblies)
        {
            token.ThrowIfCancellationRequested();
            var identity = new AssemblyName(assembly.Certificate.Identity);
            var fileName = identity.Name + ".dll";
            if (string.IsNullOrEmpty(identity.Name) || Path.GetFileName(fileName) != fileName)
                throw new InvalidDataException("Invalid certified thumbnail worker module: " + assembly.Certificate.Identity);
            // The normal deps resolver probes the private root; the engine API
            // resolver probes its selected subdirectory. Both receive the same
            // captured canonical module, never a post-initialization replacement.
            Install(Path.Combine(destination, fileName));
            var apiDirectory = Path.Combine(destination, EditorGraphicsBackend.SelectedApi);
            if (Directory.Exists(apiDirectory)) Install(Path.Combine(apiDirectory, fileName));
            void Install(string path)
            {
                if (File.Exists(path))
                {
                    var existing = AssemblyName.GetAssemblyName(path);
                    if (existing.FullName != identity.FullName)
                        throw new InvalidDataException("The deployed thumbnail worker has a conflicting module identity: " + existing.FullName
                            + "; certified canonical module: " + identity.FullName + "; source: " + assembly.Certificate.Path);
                }
                assembly.Artifact.CopyTo(path, assembly.Certificate);
            }
        }
        // Native libraries are selected from the actual live editor deployment,
        // preserving the backend that produced the certified managed closure.
        CopyNativeModules(AppContext.BaseDirectory, destination);
        var liveApi = Path.Combine(AppContext.BaseDirectory, EditorGraphicsBackend.SelectedApi);
        if (Directory.Exists(liveApi)) CopyNativeModules(liveApi, Path.Combine(destination, EditorGraphicsBackend.SelectedApi));
        return Path.Combine(destination, Path.GetFileName(original));

        void CopyDirectory(string from, string to)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A thumbnail worker deployment directory is a symbolic link: " + from);
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A thumbnail worker deployment file is a symbolic link: " + file);
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            }
            foreach (var directory in Directory.EnumerateDirectories(from))
                CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
        void CopyNativeModules(string from, string to)
        {
            foreach (var path in Directory.EnumerateFiles(from, "*.dll"))
            {
                token.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(path);
                using var image = new PEReader(stream);
                if (image.HasMetadata) continue;
                Directory.CreateDirectory(to);
                File.Copy(path, Path.Combine(to, Path.GetFileName(path)), true);
            }
        }
    }
}
