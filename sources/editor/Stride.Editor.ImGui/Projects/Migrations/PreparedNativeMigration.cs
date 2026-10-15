using System.Reflection;
using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

/// <summary>A completed native upgrade in a private tree; it owns no live editor graph.</summary>
internal sealed class PreparedNativeMigration : IDisposable
{
    internal sealed record AssetVersion(string Path, string Type, Dictionary<string, string> Versions);
    internal sealed record FileChange(string RelativePath, byte[]? BeforeHash, byte[]? AfterHash);
    internal NativeMigrationMirror Mirror { get; }
    internal FileChange[] Changes { get; }
    internal AssetVersion[] AssetVersions { get; }
    internal NativeUpgradePrompt.Decision[] Decisions { get; }
    internal bool BackupOriginals { get; }
    internal DateTimeOffset PreparedAt { get; } = DateTimeOffset.UtcNow;
    private PreparedNativeMigration(NativeMigrationMirror mirror, FileChange[] changes, AssetVersion[] versions, bool backup, NativeUpgradePrompt.Decision[] decisions)
    { Mirror = mirror; Changes = changes; AssetVersions = versions; BackupOriginals = backup; Decisions = decisions; }

    internal static PreparedNativeMigration Prepare(string path, NativeUpgradePrompt prompt, CancellationToken cancellation, string? ownedRoot = null)
    {
        var mirror = NativeMigrationMirror.Create(path, cancellation, ownedRoot);
        var borrowed = AppDomain.CurrentDomain.GetAssemblies().ToHashSet();
        PackageSession? session = null;
        var complete = false;
        try
        {
            var parameters = new PackageLoadParameters
            {
                LoadEditorPackages = true, EditorToolkit = EditorPackageLoadPolicy.Toolkit, LoadAssetsOfUnknownType = true,
                LoadMissingDependencies = true, LoadAssemblyReferences = true, AutoCompileProjects = true,
                BackupBeforeUpgrade = true, UpgradeWriteRoot = mirror.StageRoot,
                DependencyPackagePathResolver = file => mirror.StageDependency(file, cancellation),
                PackageUpgradeRequested = (package, upgrades) => prompt.Ask(package, upgrades, cancellation),
                CancelToken = cancellation, BuildConfiguration = "Debug", TemporaryAssetsInMsbuild = false, ConvertUPathToAbsolute = false,
                ExtraCompileProperties = new() { ["StrideGraphicsApi"] = EditorGraphicsBackend.SelectedApi, ["StrideGraphicsApis"] = EditorGraphicsBackend.SelectedApi, ["StrideSkipAutoPack"] = "true" }
            };
            var extension = System.IO.Path.GetExtension(mirror.StageOpenPath).ToLowerInvariant();
            if (extension is ".sln" or ".slnx")
            {
                var loaded = PackageSession.Load(mirror.StageOpenPath, parameters); session = loaded.Session;
                if (loaded.HasErrors || session == null) throw new InvalidOperationException(loaded.ToText());
            }
            else
            {
                session = EditorPackageLoadPolicy.CreateSession(); var log = new LoggerResult(); session.AddExistingProject(mirror.StageOpenPath, log, parameters);
                if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            }
            cancellation.ThrowIfCancellationRequested();
            mirror.ValidateProjectInputs();
            void Validate(string file)
            {
                if (!NativeMigrationMirror.Inside(mirror.StageRoot, file)) throw new IOException("A native upgrade attempted to write outside the staged tree: " + file);
                for (var current = System.IO.Path.GetFullPath(file); NativeMigrationMirror.Inside(mirror.StageRoot, current); current = System.IO.Path.GetDirectoryName(current)!)
                {
                    NativeMigrationMirror.RejectLink(current);
                    if (string.Equals(current, mirror.StageRoot, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            foreach (var package in session.LocalPackages)
            {
                Validate(package.FullPath.ToOSPath());
                foreach (var asset in package.Assets)
                {
                    Validate(asset.FullPath.ToOSPath());
                    var detector = new UnloadableDetector(); detector.Visit(asset.Asset);
                    if (detector.Found) throw new InvalidOperationException("Native migration left unresolved authored types: " + asset.Location);
                }
            }
            var versions = session.LocalPackages.SelectMany(package => package.Assets).Select(asset => new AssetVersion(
                System.IO.Path.GetRelativePath(mirror.StageRoot, asset.FullPath.ToOSPath()), asset.Asset.GetType().FullName ?? asset.Asset.GetType().Name,
                asset.Asset.SerializedVersion?.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()) ?? [])).ToArray();
            var saved = new LoggerResult();
            session.Save(saved, new PackageSaveParameters
            {
                PackageFilter = package => !package.IsReadOnly && NativeMigrationMirror.Inside(mirror.StageRoot, package.FullPath.ToOSPath()),
                FileRemoval = (_, file, _) => { Validate(file); File.Delete(file); },
                SolutionSave = solution => { Validate(solution.FullPath); solution.Save(); }
            });
            if (saved.HasErrors) throw new InvalidOperationException(saved.ToText());
            cancellation.ThrowIfCancellationRequested();
            var files = EnumerateOwnedFiles(mirror.StageRoot).ToDictionary(file => System.IO.Path.GetRelativePath(mirror.StageRoot, file), StringComparer.OrdinalIgnoreCase);
            var changes = new List<FileChange>();
            foreach (var relative in mirror.Baselines.Keys.Union(files.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var before = mirror.Baselines.GetValueOrDefault(relative)?.Hash;
                byte[]? after = null;
                if (files.TryGetValue(relative, out var file)) { using var stream = File.OpenRead(file); after = SHA256.HashData(stream); }
                if (before == null || after == null || !before.AsSpan().SequenceEqual(after)) changes.Add(new(relative, before, after));
            }
            var result = new PreparedNativeMigration(mirror, changes.ToArray(), versions, prompt.BackupOriginals, prompt.Decisions);
            complete = true; return result;
        }
        finally
        {
            try
            {
                if (session != null)
                {
                    foreach (var package in session.Packages)
                        package.LoadedAssemblies.RemoveAll(entry => entry.Assembly is { } assembly && (borrowed.Contains(assembly)
                            || System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly) == System.Runtime.Loader.AssemblyLoadContext.Default));
                    session.Dispose();
                }
            }
            catch { mirror.PreserveForRecovery = true; throw; }
            finally
            {
                if (!complete)
                {
                    var diagnosticsDirectory = Environment.GetEnvironmentVariable("STRIDE_EDITOR_MSBUILD_DIAGNOSTICS_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(diagnosticsDirectory))
                    {
                        // Preserve only this authenticated private failed stage;
                        // never move original inputs or retain successful mirrors.
                        mirror.PreserveForRecovery = true;
                        try
                        {
                            diagnosticsDirectory = Path.GetFullPath(diagnosticsDirectory);
                            Directory.CreateDirectory(diagnosticsDirectory);
                            var certificate = new { originalRoot = mirror.OriginalRoot, stageRoot = mirror.StageRoot,
                                stageOpenPath = mirror.StageOpenPath, retainedAtUtc = DateTime.UtcNow,
                                reason = "Failed native migration retained for opt-in MSBuild diagnostics" };
                            File.WriteAllText(Path.Combine(diagnosticsDirectory, "failed-migration-" + Guid.NewGuid().ToString("N") + ".json"),
                                System.Text.Json.JsonSerializer.Serialize(certificate));
                        }
                        catch (Exception diagnosticError)
                        {
                            System.Diagnostics.Trace.TraceError("Could not record retained migration diagnostics: " + diagnosticError);
                        }
                    }
                    mirror.Dispose();
                }
            }
        }
    }
    internal static IEnumerable<string> EnumerateOwnedFiles(string root)
    {
        NativeMigrationMirror.RejectLink(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (NativeMigrationMirror.Excluded(System.IO.Path.GetFileName(entry))) continue;
            NativeMigrationMirror.RejectLink(entry);
            if (Directory.Exists(entry)) { foreach (var file in EnumerateOwnedFiles(entry)) yield return file; }
            else yield return entry;
        }
    }
    private sealed class UnloadableDetector : AssetVisitorBase
    {
        internal bool Found { get; private set; }
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        { if (obj is IUnloadable) { Found = true; return; } base.VisitObject(obj, descriptor, visitMembers); }
    }
    public void Dispose() => Mirror.Dispose();
}
