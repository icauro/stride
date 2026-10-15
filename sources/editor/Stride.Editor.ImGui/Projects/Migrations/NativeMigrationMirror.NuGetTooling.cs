using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Stride.Editor.ImGui;

internal sealed partial class NativeMigrationMirror
{
    private sealed record ResolvedNuGetTooling(HashSet<string> Files, HashSet<string> Imports, HashSet<string> GeneratedImports);

    private ResolvedNuGetTooling InspectResolvedNuGetTooling(Project project)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var value = project.GetPropertyValue("ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(value)) return new(files, imports, generated);
        var assetsPath = Path.GetFullPath(value, Path.GetDirectoryName(project.FullPath)!);
        if (!IsPrivateProjectInput(assetsPath) || !File.Exists(assetsPath)) return new(files, imports, generated);
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = assets.RootElement;
        if (!root.TryGetProperty("project", out var authored) || !authored.TryGetProperty("restore", out var restore)
            || !restore.TryGetProperty("projectPath", out var projectPath)
            || !string.Equals(Path.GetFullPath(projectPath.GetString()!), Path.GetFullPath(project.FullPath), StringComparison.OrdinalIgnoreCase))
            return new(files, imports, generated);
        if (!root.TryGetProperty("packageFolders", out var folders) || !root.TryGetProperty("libraries", out var libraries))
            return new(files, imports, generated);

        foreach (var library in libraries.EnumerateObject())
        {
            var data = library.Value;
            if (!data.TryGetProperty("type", out var type) || type.GetString() != "package"
                || !data.TryGetProperty("path", out var packagePath) || !data.TryGetProperty("files", out var packageFiles)) continue;
            foreach (var folder in folders.EnumerateObject())
            {
                var cache = Path.GetFullPath(folder.Name);
                var package = Path.GetFullPath(packagePath.GetString()!, cache);
                if (!Inside(cache, package)) throw new IOException("Resolved NuGet package is outside its native cache root.");
                foreach (var file in packageFiles.EnumerateArray())
                {
                    var resolved = Path.GetFullPath(file.GetString()!, package);
                    if (!Inside(package, resolved)) throw new IOException("Resolved NuGet file is outside its package ownership scope.");
                    if (File.Exists(resolved)) files.Add(resolved);
                }
            }
        }

        // Only the native restore-generated entry points establish package import
        // provenance. An authored Import referencing the same cache is not enough.
        foreach (var suffix in new[] { ".nuget.g.props", ".nuget.g.targets" })
            generated.Add(Path.Combine(Path.GetDirectoryName(assetsPath)!, Path.GetFileName(project.FullPath) + suffix));
        bool changed;
        do
        {
            changed = false;
            foreach (var import in project.Imports)
            {
                var target = Path.GetFullPath(import.ImportedProject.FullPath);
                var origin = Path.GetFullPath(import.ImportingElement.ContainingProject.FullPath);
                if (files.Contains(target) && (generated.Contains(origin) || imports.Contains(origin)))
                    changed |= imports.Add(target);
            }
        } while (changed);
        return new(files, imports, generated);
    }

    private static bool IsNativeExtractedAssemblyProcessor(Project project, ProjectUsingTaskElement task, string target,
        ResolvedNuGetTooling nugetTooling, IEnumerable<string> toolRoots)
    {
        if (task.TaskName != "AssemblyProcessorTask") return false;
        var origin = Path.GetFullPath(task.ContainingProject.FullPath);
        if (!nugetTooling.Imports.Contains(origin) && !toolRoots.Any(root => Inside(root, origin))) return false;
        var sourceValue = project.GetPropertyValue("StrideAssemblyProcessorPath");
        var cachedValue = project.GetPropertyValue("StrideAssemblyProcessorTempPath");
        var framework = project.GetPropertyValue("StrideAssemblyProcessorFramework");
        var hash = project.GetPropertyValue("StrideAssemblyProcessorHash").Trim();
        var temp = project.GetPropertyValue("TEMP");
        if (string.IsNullOrWhiteSpace(sourceValue) || string.IsNullOrWhiteSpace(cachedValue) || string.IsNullOrWhiteSpace(temp)
            || string.IsNullOrWhiteSpace(framework) || framework is "." or ".." || framework.IndexOfAny(['/', '\\']) >= 0
            || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return false;
        var source = Path.GetFullPath(sourceValue, Path.GetDirectoryName(project.FullPath)!);
        var cached = Path.GetFullPath(cachedValue, Path.GetDirectoryName(project.FullPath)!);
        if (!nugetTooling.Files.Contains(source) && !toolRoots.Any(root => Inside(root, source))) return false;
        if (!File.Exists(source) || !File.Exists(source + ".hash") || !File.Exists(target)) return false;
        var expected = Path.GetFullPath(Path.Combine(temp, "stride", "AssemblyProcessor", framework, hash, Path.GetFileName(source)));
        if (!string.Equals(target, cached, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target, expected, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(File.ReadAllText(source + ".hash").Trim(), hash, StringComparison.OrdinalIgnoreCase)) return false;
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = SHA256.HashData(sourceStream);
        if (!string.Equals(Convert.ToHexString(sourceHash), hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The native assembly processor source differs from its published content hash.");
        using var cachedStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(cachedStream).AsSpan().SequenceEqual(sourceHash))
            throw new IOException("The native extracted assembly processor differs from its resolved source.");
        return true;
    }
}
