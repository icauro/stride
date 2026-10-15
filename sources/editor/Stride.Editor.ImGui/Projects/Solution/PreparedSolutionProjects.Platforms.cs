using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class PreparedSolutionProjects
{
    private (Package Package, string PreparedPath, string Destination, byte[]? Hash)[] platformMetadata = [];
    internal void AcceptPlatformFileOwnership(Func<string, byte[]?, bool> isPublished)
    {
        foreach (var entry in platformMetadata.ToArray())
        {
            if (!isPublished(entry.Destination, entry.Hash)) throw new IOException("The platform metadata publication is not owned by its native journal.");
            entry.Package.AcceptPreparedMetadataPublication(entry.PreparedPath, entry.Destination, entry.Hash);
            platformMetadata = platformMetadata.Where(remaining => !ReferenceEquals(remaining.Package, entry.Package)).ToArray();
        }
    }
    internal static PreparedSolutionProjects PreparePlatformProjects(PreparedPackagePlatformUpdate output, CancellationToken cancellation)
    {
        var borrowed = AppDomain.CurrentDomain.GetAssemblies().ToHashSet();
        PackageSession? session = EditorPackageLoadPolicy.CreateSession();
        try
        {
            var log = new LoggerResult();
            var parameters = new PackageLoadParameters
            {
                LoadEditorPackages = true, EditorToolkit = EditorPackageLoadPolicy.Toolkit, LoadAssetsOfUnknownType = true,
                LoadMissingDependencies = true, LoadAssemblyReferences = false, AutoCompileProjects = false,
                TemporaryAssetsInMsbuild = false, ConvertUPathToAbsolute = false, CancelToken = cancellation,
                PackageUpgradeRequested = (_, _) => PackageUpgradeRequestedAnswer.DoNotUpgradeAny,
                UpgradeWriteRoot = output.Mirror.StageRoot,
                DependencyPackagePathResolver = file => output.Mirror.StageDependency(file, cancellation),
                BuildConfiguration = "Debug",
                ExtraCompileProperties = new() { ["StrideGraphicsApi"] = EditorGraphicsBackend.SelectedApi,
                    ["StrideGraphicsApis"] = EditorGraphicsBackend.SelectedApi, ["StrideSkipAutoPack"] = "true" },
            };
            var paths = output.Projects.Where(project => project.RemovedId == null && project.Path.Length != 0)
                .Select(project => project.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var final in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                var staged = Path.Combine(output.Mirror.StageRoot, Path.GetRelativePath(output.Mirror.OriginalRoot, final));
                session.AddExistingProject(new UFile(staged), log, parameters);
                if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            }
            cancellation.ThrowIfCancellationRequested();
            foreach (var package in session.Packages)
            {
                if (package.State < PackageState.AssetsReady) throw new InvalidOperationException("The native platform project did not finish loading.");
                foreach (var item in package.Assets)
                {
                    var detector = new UnloadableDetector(); detector.Visit(item.Asset);
                    if (detector.Found) throw new InvalidOperationException("The platform project contains unresolved authored types: " + item.Location);
                }
            }
            output.Mirror.ValidateExternalProjectInputs();
            string Map(string path) => output.Mirror.OriginalPackagePath(path);
            var certificates = new List<(Package, string, string, byte[]?)>();
            foreach (var container in session.Projects)
            {
                var package = container.Package;
                if (container is SolutionProject generated && paths.Any(path => string.Equals(path, Map(generated.FullPath.ToOSPath()), StringComparison.OrdinalIgnoreCase)))
                {
                    var preparedPath = package.FullPath.ToOSPath();
                    if (!package.TryGetMetadataDiskBaseline(preparedPath, out var hash)) throw new InvalidOperationException("The native generated metadata has no load certificate.");
                    certificates.Add((package, preparedPath, Map(preparedPath), hash));
                }
                foreach (var item in package.Assets)
                    if (item.SourceFolder is { IsAbsolute: true } source) item.SourceFolder = new UDirectory(Map(source.ToOSPath()));
                foreach (var folder in package.AssetFolders)
                    if (folder.Path.IsAbsolute) folder.Path = new UDirectory(Map(folder.Path.ToOSPath()));
                package.SetPackagePath(new UFile(Map(package.FullPath.ToOSPath())), false);
                if (container is SolutionProject project) project.VSProject.FullPath = Map(project.FullPath.ToOSPath());
                foreach (var dependency in container.DirectDependencies)
                    if (!string.IsNullOrEmpty(dependency.MSBuildProject) && Path.IsPathRooted(dependency.MSBuildProject)) dependency.MSBuildProject = Map(dependency.MSBuildProject);
                foreach (var dependency in container.FlattenedDependencies)
                    if (!string.IsNullOrEmpty(dependency.MSBuildProject) && Path.IsPathRooted(dependency.MSBuildProject)) dependency.MSBuildProject = Map(dependency.MSBuildProject);
            }
            return new PreparedSolutionProjects(session, paths.FirstOrDefault() ?? output.Mirror.OriginalOpenPath, borrowed) { platformMetadata = certificates.ToArray() };
        }
        catch
        {
            var failed = new FailedPreparationResources(session, null, borrowed);
            EditorBackgroundJobRetirement.Retire(Task.FromResult(failed), null, owner => owner.Dispose(), "Platform project preparation");
            throw;
        }
    }
}
