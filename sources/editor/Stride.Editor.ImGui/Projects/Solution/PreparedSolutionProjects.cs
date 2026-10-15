using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Visitors;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

/// <summary>An isolated native session; no live workspace is touched by preparation.</summary>
internal sealed partial class PreparedSolutionProjects : IDisposable
{
    private readonly HashSet<Assembly> borrowed;
    private readonly NativeMigrationMirror? mirror;
    private bool sessionDisposed;
    internal PackageSession Session { get; }
    internal PackageContainer[] Projects { get; }
    internal string SourcePath { get; }
    internal LoadedAssembly[] Assemblies { get; }
    internal bool IsRetained { get; set; }
    private bool disposed;
    private PreparedSolutionProjects(PackageSession session, string path, HashSet<Assembly> borrowed, NativeMigrationMirror? mirror = null)
    {
        Session = session; SourcePath = path; this.borrowed = borrowed; this.mirror = mirror;
        Projects = session.Projects.ToArray(); Assemblies = session.AssemblyContainer.LoadedAssemblies.ToArray();
        ProtectBorrowedAssemblies();
    }
    internal string OriginalPackagePath(Package package)
        => mirror?.OriginalPackagePath(package.FullPath.ToOSPath()) ?? package.FullPath.ToOSPath();
    internal static PreparedSolutionProjects Prepare(string path, CancellationToken cancellation, bool dependencyPackage = false, NativeUpgradePrompt? upgradePrompt = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".csproj" or ".sln" or ".slnx" or ".sdpkg")) throw new InvalidOperationException("Select a supported C# project or solution.");
        var borrowed = AppDomain.CurrentDomain.GetAssemblies().ToHashSet();
        PackageSession? session = null;
        NativeMigrationMirror? mirror = null;
        try
        {
            var loadPath = path;
            if (dependencyPackage) { mirror = NativeMigrationMirror.Create(path, cancellation); loadPath = mirror.StageOpenPath; }
            var parameters = new PackageLoadParameters
            {
                LoadEditorPackages = true, EditorToolkit = EditorPackageLoadPolicy.Toolkit, LoadAssetsOfUnknownType = true,
                LoadMissingDependencies = true, LoadAssemblyReferences = true, AutoCompileProjects = !dependencyPackage,
                PackageUpgradeRequested = (package, upgrades) => upgradePrompt?.Ask(package, upgrades, cancellation) ?? PackageUpgradeRequestedAnswer.DoNotUpgradeAny,
                CancelToken = cancellation, BuildConfiguration = "Debug", TemporaryAssetsInMsbuild = false,
                ConvertUPathToAbsolute = false,
                UpgradeWriteRoot = mirror?.StageRoot,
                DependencyPackagePathResolver = mirror == null ? null : file => mirror.StageDependency(file, cancellation),
                ExtraCompileProperties = new() { ["StrideGraphicsApi"] = EditorGraphicsBackend.SelectedApi, ["StrideGraphicsApis"] = EditorGraphicsBackend.SelectedApi, ["StrideSkipAutoPack"] = "true" }
            };
            if (extension is ".sln" or ".slnx")
            {
                var result = PackageSession.Load(loadPath, parameters); session = result.Session;
                if (result.HasErrors || session == null) throw new InvalidOperationException(result.ToText());
            }
            else
            {
                session = EditorPackageLoadPolicy.CreateSession(); var log = new LoggerResult(); session.AddExistingProject(loadPath, log, parameters);
                if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            }
            cancellation.ThrowIfCancellationRequested();
            if (!dependencyPackage && !session.Projects.OfType<SolutionProject>().Any()) throw new InvalidOperationException("The selected package has no supported solution project.");
            if (dependencyPackage && session.Packages.Any(package => package.State < PackageState.AssetsReady)) throw new NativeMigrationRequiredException();
            foreach (var item in session.Packages.SelectMany(package => package.Assets))
            {
                var detector = new UnloadableDetector(); detector.Visit(item.Asset);
                if (detector.Found) throw new InvalidOperationException("The imported project contains unresolved authored types: " + item.Location);
            }
            if (mirror != null) SavePrivateLibrary(session, mirror, upgradePrompt, cancellation);
            return new(session, path, borrowed, mirror);
        }
        catch
        {
            var failed = new FailedPreparationResources(session, mirror, borrowed);
            EditorBackgroundJobRetirement.Retire(Task.FromResult(failed), null, owner => owner.Dispose(), "Failed solution preparation");
            throw;
        }
    }
    private sealed class FailedPreparationResources(PackageSession? session, NativeMigrationMirror? mirror, HashSet<Assembly> borrowed) : IDisposable
    {
        private bool sessionReleased = session == null;
        private bool mirrorReleased = mirror == null;
        public void Dispose()
        {
            if (!sessionReleased)
            {
                foreach (var package in session!.Packages)
                    package.LoadedAssemblies.RemoveAll(entry => entry.Assembly != null && IsBorrowed(entry.Assembly, borrowed));
                session.Dispose(); sessionReleased = true;
            }
            if (!mirrorReleased) { mirror!.Dispose(); mirrorReleased = true; }
        }
    }
    private static void SavePrivateLibrary(PackageSession session, NativeMigrationMirror mirror, NativeUpgradePrompt? prompt, CancellationToken cancellation)
    {
        void Validate(string path)
        {
            if (!NativeMigrationMirror.Inside(mirror.StageRoot, path)) throw new IOException("Library migration attempted to write outside its private mirror: " + path);
            for (var current = System.IO.Path.GetFullPath(path); NativeMigrationMirror.Inside(mirror.StageRoot, current); current = System.IO.Path.GetDirectoryName(current)!)
            { NativeMigrationMirror.RejectLink(current); if (string.Equals(current, mirror.StageRoot, StringComparison.OrdinalIgnoreCase)) break; }
        }
        var log = new LoggerResult();
        foreach (var package in session.Packages.Where(package => NativeMigrationMirror.Inside(mirror.StageRoot, package.FullPath.ToOSPath())))
        {
            cancellation.ThrowIfCancellationRequested(); Validate(package.FullPath.ToOSPath());
            foreach (var asset in package.Assets)
            {
                Validate(asset.FullPath.ToOSPath());
                if (asset.IsDirty && !Package.SaveSingleAsset(asset, log)) throw new InvalidOperationException(log.ToText());
            }
            if (package.IsDirty)
            {
                AssetFileSerializer.Save(package.FullPath, package, null); package.IsDirty = false;
            }
        }
        File.WriteAllText(System.IO.Path.Combine(mirror.StageRoot, ".stride-library-migration.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, originalPackagePath = mirror.OriginalOpenPath, preparedAt = DateTimeOffset.UtcNow,
            decisions = prompt?.Decisions ?? [], originalsUnchanged = true,
            packages = session.Packages.Select(package => new { originalPath = mirror.OriginalPackagePath(package.FullPath.ToOSPath()),
                stagedPath = package.FullPath.ToOSPath(), name = package.Meta.Name, version = package.Meta.Version?.ToString(),
                assets = package.Assets.Select(asset => asset.Id.ToString()) }),
            originalFiles = mirror.Baselines.Values.Select(baseline => new { path = baseline.RelativePath, sha256 = Convert.ToHexString(baseline.Hash) })
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    private void ProtectBorrowedAssemblies()
    {
        // Package loading may reuse host assemblies without registering them. An
        // isolated session must not later unregister those borrowed registrations.
        foreach (var package in Session.Packages)
            package.LoadedAssemblies.RemoveAll(entry => entry.Assembly != null && IsBorrowed(entry.Assembly, borrowed));
    }
    private static bool IsBorrowed(Assembly assembly, HashSet<Assembly> existing) => existing.Contains(assembly)
        || System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly) == System.Runtime.Loader.AssemblyLoadContext.Default;
    private sealed class UnloadableDetector : AssetVisitorBase
    {
        internal bool Found { get; private set; }
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (obj is IUnloadable) { Found = true; return; }
            base.VisitObject(obj, descriptor, visitMembers);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        if (!sessionDisposed) { ProtectBorrowedAssemblies(); Session.Dispose(); sessionDisposed = true; }
        mirror?.Dispose(); disposed = true;
    }
}
