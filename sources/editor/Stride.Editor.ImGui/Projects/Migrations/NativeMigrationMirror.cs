using System.Security.Cryptography;
using System.Xml.Linq;

namespace Stride.Editor.ImGui;

internal sealed partial class NativeMigrationMirror : IDisposable
{
    internal sealed record Baseline(string RelativePath, byte[] Hash);
    private readonly string container;
    private readonly string marker;
    internal string OriginalRoot { get; }
    internal string StageRoot { get; }
    internal string OriginalOpenPath { get; }
    internal string StageOpenPath { get; }
    internal string RecoveryRoot => Path.Combine(container, "recovery");
    internal bool PreserveForRecovery { get; set; }
    internal Dictionary<string, Baseline> Baselines { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> stagedDependencyRoots = new(StringComparer.OrdinalIgnoreCase);
    internal string OriginalPackagePath(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var input in boundedProjectInputs)
            if (string.Equals(full, input.Value, StringComparison.OrdinalIgnoreCase)) return input.Key;
        foreach (var input in externalProjectRoots.OrderByDescending(entry => entry.Value.Length))
            if (Inside(input.Value, full)) return Path.Combine(input.Key, Path.GetRelativePath(input.Value, full));
        foreach (var dependency in stagedDependencyRoots)
            if (Inside(dependency.Value, full)) return Path.Combine(dependency.Key, Path.GetRelativePath(dependency.Value, full));
        return Inside(StageRoot, full) ? Path.Combine(OriginalRoot, Path.GetRelativePath(StageRoot, full)) : full;
    }
    internal string StageDependency(string path, CancellationToken cancellation)
    {
        path = Path.GetFullPath(path);
        if (IsPrivateProjectInput(path)) return path;
        if (Inside(OriginalRoot, path)) return Path.Combine(StageRoot, Path.GetRelativePath(OriginalRoot, path));
        foreach (var dependency in stagedDependencyRoots.OrderByDescending(entry => entry.Key.Length))
            if (Inside(dependency.Key, path)) return Path.Combine(dependency.Value, Path.GetRelativePath(dependency.Key, path));
        // Native companions may have no authored .sdpkg: their actual .csproj
        // supplies the package root. Linked manifest assets are real file inputs
        // too; adjacent source files share one private directory snapshot.
        if (!File.Exists(path)) throw new FileNotFoundException("The native dependency input is unavailable.", path);
        var sourceRoot = Path.GetDirectoryName(path)!;
        if (string.Equals(sourceRoot, Path.GetPathRoot(sourceRoot), StringComparison.OrdinalIgnoreCase)
            || Inside(sourceRoot, OriginalRoot) || Inside(sourceRoot, StageRoot))
            throw new IOException("The native dependency input requires a bounded source directory: " + path);
        RejectLink(path);
        for (var current = sourceRoot; current != null; current = Path.GetDirectoryName(current)) RejectLink(current);
        var targetRoot = Path.Combine(StageRoot, ".dependency-libraries", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetRoot);
        void Copy(string directory)
        {
            RejectLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellation.ThrowIfCancellationRequested(); if (Excluded(Path.GetFileName(entry))) continue; RejectLink(entry);
                var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, entry));
                if (Directory.Exists(entry)) { Directory.CreateDirectory(target); Copy(entry); }
                else
                {
                    using var source = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); source.CopyTo(output);
                }
            }
        }
        Copy(sourceRoot);
        var staged = Path.Combine(targetRoot, Path.GetFileName(path));
        if (!File.Exists(staged)) throw new IOException("The native dependency input was excluded from its private snapshot: " + path);
        stagedDependencyRoots.Add(sourceRoot, targetRoot); return staged;
    }
    private NativeMigrationMirror(string path, string? ownedRoot)
    {
        OriginalOpenPath = Path.GetFullPath(path); OriginalRoot = Path.GetFullPath(ownedRoot ?? Path.GetDirectoryName(OriginalOpenPath)!);
        if (!Inside(OriginalRoot, OriginalOpenPath)) throw new IOException("The migration entry point is outside the owned project root.");
        for (var current = OriginalRoot; current != null; current = Path.GetDirectoryName(current)) RejectLink(current);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor", "Migrations");
        Directory.CreateDirectory(parent); container = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(container);
        marker = Path.Combine(container, ".stride-native-migration"); File.WriteAllText(marker, OriginalRoot);
        StageRoot = Path.Combine(container, "project"); Directory.CreateDirectory(StageRoot);
        StageOpenPath = Path.Combine(StageRoot, Path.GetRelativePath(OriginalRoot, OriginalOpenPath));
    }
    internal static NativeMigrationMirror Create(string path, CancellationToken cancellation, string? ownedRoot = null, bool deferProjectValidation = false)
    {
        var mirror = new NativeMigrationMirror(path, ownedRoot);
        try { mirror.CopyDirectory(mirror.OriginalRoot, cancellation); if (!deferProjectValidation) mirror.ValidateProjectInputs(); return mirror; }
        catch (Exception failure)
        {
            try { mirror.Dispose(); }
            catch (Exception retirement)
            {
                EditorBackgroundJobRetirement.Retire(Task.FromResult(mirror), null, owner => owner.Dispose(), "Failed native mirror preparation");
                throw new AggregateException("Native mirror preparation and retirement failed.", failure, retirement);
            }
            throw;
        }
    }
    private void CopyDirectory(string directory, CancellationToken cancellation)
    {
        RejectLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellation.ThrowIfCancellationRequested(); var name = Path.GetFileName(entry);
            if (Excluded(name)) continue; RejectLink(entry);
            var relative = Path.GetRelativePath(OriginalRoot, entry); var target = Path.Combine(StageRoot, relative);
            if (Directory.Exists(entry)) { Directory.CreateDirectory(target); CopyDirectory(entry, cancellation); }
            else
            {
                using var source = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(output); source.Position = 0; Baselines.Add(relative, new(relative, SHA256.HashData(source)));
            }
        }
    }
    internal static bool Excluded(string name) => name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".stride-backup-", StringComparison.OrdinalIgnoreCase);
    internal static void RejectLink(string path)
    { if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Migration cannot follow a linked path: " + path); }
    internal void ValidateProjectInputs(IEnumerable<string>? preparedProjects = null)
    {
        foreach (var file in Baselines.Values.Where(file => Path.GetExtension(file.RelativePath).ToLowerInvariant() is ".sln" or ".slnx"))
        {
            var path = Path.Combine(StageRoot, file.RelativePath);
            var solution = Stride.Core.Solutions.Solution.FromFile(path);
            foreach (var project in solution.Projects.Where(project => !project.IsSolutionFolder))
                if (!IsPrivateProjectInput(Path.GetFullPath(project.FullPath, Path.GetDirectoryName(path)!)))
                    throw new IOException("Migration requires the external solution project root to be staged: " + project.FullPath);
        }
        foreach (var path in preparedProjects ?? Baselines.Values.Where(file => Path.GetExtension(file.RelativePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(project => Path.Combine(StageRoot, project.RelativePath)))
        {
            if (!IsPrivateProjectInput(path)) throw new IOException("The prepared project is outside its private stage.");
            RejectLink(path); var xml = XDocument.Load(path);
            foreach (var item in xml.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "Compile"))
            {
                var include = item.Attribute("Include")?.Value; if (string.IsNullOrWhiteSpace(include)) continue;
                if (item.Name.LocalName == "ProjectReference" && include.Contains("$(", StringComparison.Ordinal))
                    throw new IOException("Migration requires an explicitly resolved local project reference: " + include);
                if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*')) continue;
                foreach (var itemPath in include.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var target = Path.GetFullPath(itemPath, Path.GetDirectoryName(path)!);
                    if (!IsPrivateProjectInput(target)) throw new IOException("Migration requires the external local project/source root to be staged: " + include);
                }
            }
            var evaluated = Stride.Core.Assets.VSProjectHelper.LoadProject(path, extraProperties: new()
            { ["StrideGraphicsApi"] = EditorGraphicsBackend.SelectedApi, ["StrideGraphicsApis"] = EditorGraphicsBackend.SelectedApi });
            try
            {
                var toolRoots = new[] { "MSBuildSDKsPath", "MSBuildToolsPath", "MSBuildExtensionsPath", "MSBuildExtensionsPath32" }
                    .Select(evaluated.GetPropertyValue).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var resolved in evaluated.Imports.Where(import => import.SdkResult != null))
                {
                    if (resolved.SdkResult!.Path is { Length: > 0 } sdkPath) toolRoots.Add(Path.GetFullPath(sdkPath));
                    if (resolved.SdkResult.AdditionalPaths != null)
                        foreach (var additionalSdkPath in resolved.SdkResult.AdditionalPaths) toolRoots.Add(Path.GetFullPath(additionalSdkPath));
                }
                var nugetTooling = InspectResolvedNuGetTooling(evaluated);
                foreach (var import in evaluated.Imports)
                {
                    var importedPath = import.ImportedProject.FullPath;
                    if (import.SdkResult != null || toolRoots.Any(root => Inside(root, importedPath))) continue;
                    var origin = Path.GetFullPath(import.ImportingElement.ContainingProject.FullPath);
                    if (nugetTooling.Imports.Contains(Path.GetFullPath(importedPath))
                        && (nugetTooling.Imports.Contains(origin) || nugetTooling.GeneratedImports.Contains(origin))) continue;
                    if (!IsPrivateProjectInput(importedPath))
                        throw new IOException("The native evaluated authored import is outside its private ownership scope: " + importedPath);
                }
                foreach (var item in evaluated.Items.Where(item => item.ItemType is "ProjectReference" or "Compile" or "Content" or "None" or "EmbeddedResource" or "AdditionalFiles" or "Analyzer" or "Resource" or "Page" or "ApplicationDefinition" or "NativeReference" or "_DeploymentManifestIconFile"))
                {
                    var inputPath = Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(path)!);
                    // SDK-imported analyzers are compiler tooling, not authored
                    // migration output. Require both provenance and target scope;
                    // an authored external Analyzer still needs private staging.
                    if (item.ItemType == "Analyzer" && IsSdkAnalyzerInput(inputPath, item.Xml.ContainingProject.FullPath, toolRoots)) continue;
                    // Native package-provided source-generator inputs (for example
                    // Stride's AdditionalFiles content-type map) are readonly
                    // compiler data. Authored AdditionalFiles still need staging.
                    if (item.ItemType is "Analyzer" or "AdditionalFiles" && nugetTooling.Files.Contains(inputPath)
                        && (nugetTooling.Imports.Contains(Path.GetFullPath(item.Xml.ContainingProject.FullPath))
                            || nugetTooling.GeneratedImports.Contains(Path.GetFullPath(item.Xml.ContainingProject.FullPath))
                            || toolRoots.Any(root => Inside(root, item.Xml.ContainingProject.FullPath)))) continue;
                    if (!IsPrivateProjectInput(inputPath))
                        throw new IOException("Migration requires the evaluated local project/source root to be staged: " + item.EvaluatedInclude);
                }
                foreach (var task in evaluated.GetLogicalProject().OfType<Microsoft.Build.Construction.ProjectUsingTaskElement>())
                {
                    if (string.IsNullOrWhiteSpace(task.AssemblyFile)) continue;
                    var assembly = evaluated.ExpandString(task.AssemblyFile);
                    var target = Path.GetFullPath(assembly, Path.GetDirectoryName(task.ContainingProject.FullPath)!);
                    if (File.Exists(target) && !IsPrivateProjectInput(target) && !toolRoots.Any(root => Inside(root, target))
                        && !(nugetTooling.Files.Contains(target) && nugetTooling.Imports.Contains(Path.GetFullPath(task.ContainingProject.FullPath)))
                        && !IsNativeExtractedAssemblyProcessor(evaluated, task, target, nugetTooling, toolRoots))
                        throw new IOException("The native task assembly is outside its private input ownership scope: " + target);
                }
                foreach (var reference in evaluated.GetItems("Reference"))
                {
                    var hint = reference.GetMetadataValue("HintPath");
                    if (!string.IsNullOrWhiteSpace(hint) && !IsPrivateProjectInput(Path.GetFullPath(hint, Path.GetDirectoryName(path)!)))
                        throw new IOException("The native reference file is outside its private ownership scope: " + hint);
                }
                foreach (var name in AuthoredProjectProperties.NativeCompilerFileProperties)
                {
                    var value = evaluated.GetPropertyValue(name);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var target = Path.GetFullPath(value, Path.GetDirectoryName(path)!);
                    if (!IsPrivateProjectInput(target) && !toolRoots.Any(root => Inside(root, target)))
                        throw new IOException("The native compiler resource is outside its private input ownership scope: " + name + "=" + value);
                }
                foreach (var name in new[] { "OutputPath", "OutDir", "BaseOutputPath", "IntermediateOutputPath", "BaseIntermediateOutputPath", "MSBuildProjectExtensionsPath" })
                {
                    var value = evaluated.GetPropertyValue(name);
                    if (!string.IsNullOrWhiteSpace(value) && !IsPrivateProjectInput(Path.GetFullPath(value, Path.GetDirectoryName(path)!)))
                        throw new IOException("Migration requires a staged native build output: " + name + "=" + value);
                }
            }
            finally { evaluated.ProjectCollection.UnloadAllProjects(); evaluated.ProjectCollection.Dispose(); }
        }
    }

    private static bool IsSdkAnalyzerInput(string inputPath, string definingProjectPath, IEnumerable<string> toolRoots)
        => toolRoots.Any(root => Inside(root, inputPath))
            && toolRoots.Any(root => Inside(root, definingProjectPath));
    internal static bool Inside(string root, string path)
    { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
    public void Dispose()
    {
        if (PreserveForRecovery) return;
        // Delete only the authenticated private preparation directory.
        var resolved = Path.GetFullPath(container); var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor", "Migrations"));
        if (Path.GetDirectoryName(resolved) == expected && File.Exists(marker) && File.ReadAllText(marker) == OriginalRoot) Directory.Delete(resolved, true);
    }
}
