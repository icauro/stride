using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed record AssetEntry(string Path, string Name, string Extension, Type AssetType, AssetId Id, string[] Tags)
{
    public string UiId { get; } = Id.ToString();
    public string NaturalName { get; } = Name.Replace(" ", "");
    public string TypeLabel { get; } = Stride.Core.DisplayAttribute.GetDisplayName(AssetType) ?? AssetType.Name;
    public int TypeOrder { get; } = Stride.Core.DisplayAttribute.GetOrder(AssetType) ?? 0;
}

/// <summary>Real package discovery and compiled content for the engine-backed editor views.</summary>
internal sealed partial class ProjectWorkspace
{
    private readonly Dictionary<AssetId, AssetItem> assetsById = [];
    public string PackagePath { get; private set; }
    public string OpenPath { get; private set; }
    public string BuildProjectPath { get; private set; }
    public string Name { get; private set; }
    public IReadOnlyList<AssetEntry> Assets { get; private set; }
    public Package? Package { get; private set; }
    public bool HasActivePackage => Package != null;
    public PackageSession Session { get; }
    public IReadOnlyList<ILogMessage> NativeLoadDiagnostics { get; private set; } = [];
    public string CompiledOutput { get; private set; } = "";
    public bool HasCompiledContent => CompiledOutput.Length != 0;
    public Exception? CompilationFailure { get; private set; }
    internal void SetActiveProject(SolutionProject project, string output, Exception? failure = null)
    {
        if (!Session.Projects.Contains(project)) throw new InvalidOperationException("The project no longer belongs to this session.");
        if (output.Length != 0 && project.Package.State < PackageState.AssetsReady) throw new InvalidOperationException("The project has not loaded successfully.");
        Session.CurrentProject = project;
        Package = project.Package; PackagePath = Package.FullPath.ToOSPath(); BuildProjectPath = project.FullPath.ToOSPath();
        Name = Package.Meta.Name ?? Path.GetFileNameWithoutExtension(PackagePath); CompiledOutput = output; CompilationFailure = failure;
    }
    public IReadOnlyList<AssetItem> AssetItems { get; private set; }
    public IReadOnlyList<SolutionProject> Projects { get; private set; } = [];
    public bool HasPackageChanges => Session.IsDirty || Session.LocalPackages.Any(package => package.IsDirty);
    public AssetItem FindAsset(string path) => AssetItems.Single(item => string.Equals(item.FullPath.ToOSPath(), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    public AssetItem FindAsset(AssetId id) => assetsById.TryGetValue(id, out var asset) ? asset : throw new InvalidOperationException("The asset no longer exists.");
    public void RefreshAssets()
    {
        Projects = Session.Projects.OfType<SolutionProject>().ToArray();
        var packages = Session.Packages.Where(package => package.State >= PackageState.AssetsReady).Distinct();
        AssetItems = packages.SelectMany(package => package.Assets).DistinctBy(item => item.Id).ToArray();
        assetsById.Clear(); foreach (var asset in AssetItems) assetsById.Add(asset.Id, asset);
        Assets = AssetItems.Where(item => !item.Package.IsReadOnly).OrderBy(item => item.Location.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(item => new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), Path.GetExtension(item.FullPath.ToOSPath()), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray())).ToArray();
    }
    public void SavePackageChanges(Action<Package, string, PackageFileRemovalKind>? fileRemoval = null,
        Func<SolutionProject, AssetItem, bool, bool>? projectItemChanges = null,
        Action<Stride.Core.Solutions.Solution>? solutionSave = null, Action<Package, byte[]>? packageWrite = null)
    {
        var log = new LoggerResult();
        // Authored document graphs own asset saves; never overwrite them with the
        // package session's older in-memory asset objects during metadata saving.
        Session.Save(log, new PackageSaveParameters { AssetFilter = _ => false, PackageFilter = package => !package.IsReadOnly && package.State >= PackageState.AssetsReady,
            FileRemoval = fileRemoval, ProjectItemChanges = projectItemChanges, SolutionSave = solutionSave, PackageWrite = packageWrite });
        if (log.HasErrors) throw new InvalidOperationException(log.ToText());
    }
    private bool unadoptedSessionReleased;
    internal void ReleaseUnadopted()
    {
        if (!unadoptedSessionReleased) { Session.Dispose(); unadoptedSessionReleased = true; }
        if (CompiledOutput.Length != 0)
        { OwnedProjectBuildOutput.Release(CompiledOutput); CompiledOutput = ""; }
    }

    public ProjectWorkspace(string packagePath, CancellationToken cancellation = default, Action<ProjectLoadState>? progress = null, string? selectedProject = null,
        ProjectWorkspaceLoadMode loadMode = ProjectWorkspaceLoadMode.FullCompilation)
    {
        OpenPath = Path.GetFullPath(packagePath);
        var log = new LoggerResult();
        var migrationRequired = false;
        var solution = string.Equals(Path.GetExtension(OpenPath), ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(OpenPath), ".slnx", StringComparison.OrdinalIgnoreCase);
        if (loadMode == ProjectWorkspaceLoadMode.PreparedAuthoredFixture)
        {
            if (solution || !string.Equals(Path.GetExtension(OpenPath), ".sdpkg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Prepared authored fixture loading requires a package fixture.");
            PreparedAuthoredFixture.ValidateBeforeLoad(OpenPath);
        }
        var parameters = new PackageLoadParameters
        {
            LoadEditorPackages = true, EditorToolkit = EditorPackageLoadPolicy.Toolkit, LoadAssetsOfUnknownType = true,
            LoadMissingDependencies = true, LoadAssemblyReferences = true, AutoCompileProjects = solution,
            PackageUpgradeRequested = (_, _) => { migrationRequired = true; return PackageUpgradeRequestedAnswer.DoNotUpgradeAny; },
            CancelToken = cancellation, BuildConfiguration = "Debug",
            TemporaryAssetsInMsbuild = false, ConvertUPathToAbsolute = false,
            ExtraCompileProperties = new Dictionary<string, string>
            { ["StrideGraphicsApi"] = EditorGraphicsBackend.SelectedApi, ["StrideGraphicsApis"] = EditorGraphicsBackend.SelectedApi, ["StrideSkipAutoPack"] = "true" },
        };
        if (solution)
        {
            progress?.Invoke(new ProjectLoadState("Loading solution", "Resolving real solution projects and assemblies..."));
            var loaded = PackageSession.Load(OpenPath, parameters);
            if (migrationRequired || loaded.Session == null || loaded.OperationCancelled)
            {
                Exception failure = cancellation.IsCancellationRequested ? new OperationCanceledException(cancellation)
                    : migrationRequired ? new NativeMigrationRequiredException() : new InvalidOperationException(loaded.ToText());
                if (loaded.Session is { } failedSession)
                {
                    try { failedSession.Dispose(); }
                    catch (Exception cleanupFailure)
                    {
                        EditorBackgroundJobRetirement.Retire(Task.FromResult(failedSession), null, session => session.Dispose(), "Failed project session");
                        throw new AggregateException(failure, cleanupFailure);
                    }
                }
                throw failure;
            }
            Session = loaded.Session;
            NativeLoadDiagnostics = loaded.Messages.ToArray();
        }
        else
        {
            try { if (loadMode == ProjectWorkspaceLoadMode.FullCompilation) CompiledOutput = CompiledProjectBuild.Build(OpenPath, cancellation, progress); }
            catch (NativeMigrationRequiredException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { CompilationFailure = error; }
            Session = EditorPackageLoadPolicy.CreateSession();
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (solution)
            {
                var allProjects = Session.Projects.OfType<SolutionProject>().ToArray();
                var projects = allProjects.Where(project => project.Package.State >= PackageState.AssetsReady).ToArray();
                if (projects.Length == 0) projects = allProjects;
                if (Session.LocalPackages.SelectMany(package => package.Assets).Any(asset => asset.IsDirty)) throw new NativeMigrationRequiredException();
                var active = projects.SingleOrDefault(project => selectedProject != null && string.Equals(project.FullPath.ToOSPath(), selectedProject, StringComparison.OrdinalIgnoreCase))
                    ?? projects.FirstOrDefault(project => project.Type == ProjectType.Executable && project.Platform == Stride.Core.PlatformType.Windows)
                    ?? projects.FirstOrDefault();
                if (active == null)
                {
                    Session.CurrentProject = null!; Package = null; BuildProjectPath = "";
                }
                else
                {
                    Session.CurrentProject = active; Package = active.Package; BuildProjectPath = active.FullPath.ToOSPath();
                }

            }
            else
            {
                var container = Session.AddExistingProject(OpenPath, log, parameters);
                if (migrationRequired || Session.LocalPackages.SelectMany(package => package.Assets).Any(asset => asset.IsDirty)) throw new NativeMigrationRequiredException();
                Package = container.Package;
                BuildProjectPath = container is SolutionProject project ? project.FullPath.ToOSPath() : Path.ChangeExtension(OpenPath, ".csproj");
                if (container is SolutionProject current) Session.CurrentProject = current;
            }
            cancellation.ThrowIfCancellationRequested();
            if (!solution)
            {
                NativeLoadDiagnostics = log.Messages.ToArray();

            }
            PackagePath = Package?.FullPath.ToOSPath() ?? "";
            Name = Package?.Meta.Name ?? Path.GetFileNameWithoutExtension(Package == null ? OpenPath : PackagePath);
            Projects = Session.Projects.OfType<SolutionProject>().ToArray();
            RefreshAssets();
            if (loadMode == ProjectWorkspaceLoadMode.PreparedAuthoredFixture) PreparedAuthoredFixture.ValidateLoadedSession(OpenPath, Session);
            if (Package is { } loadedPackage && loadedPackage.State < PackageState.AssetsReady)
            {
                if (CompiledOutput.Length != 0) OwnedProjectBuildOutput.Release(CompiledOutput);
                CompiledOutput = "";
                CompilationFailure ??= new InvalidOperationException(NativeLoadDiagnostics.Count != 0
                    ? string.Join(Environment.NewLine, NativeLoadDiagnostics.Select(message => message.ToString())) : "The native project did not finish loading.");
            }
            else if (solution && Package != null)
            {
                try { CompiledOutput = CompiledProjectBuild.Build(BuildProjectPath, cancellation, progress); }
                catch (NativeMigrationRequiredException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { CompilationFailure = error; }
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            try { ReleaseUnadopted(); }
            catch (Exception cleanupFailure)
            {
                EditorBackgroundJobRetirement.Retire(Task.FromResult(this), null, failed => failed.ReleaseUnadopted(), "Failed project workspace");
                throw new AggregateException(failure, cleanupFailure);
            }
            throw;
        }
    }
}
