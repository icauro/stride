using System.Reflection;
using NuGet.ProjectModel;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record PackageDependencyRemovalScope(string[] AssemblyPaths, string[] ProjectPaths, Assembly[] LoadedAssemblies);

internal sealed partial class PackageDependencies
{
    public PackageDependencyRemovalScope GetRemovalScope(SolutionProject project, DependencyRange removing, ProjectDependencyEvaluation? evaluation = null, LockFile? resolvedGraph = null)
        => GetRemovalScope(project, [removing], evaluation, resolvedGraph);

    public PackageDependencyRemovalScope GetRemovalScope(SolutionProject project, IReadOnlyCollection<DependencyRange> removing,
        ProjectDependencyEvaluation? evaluation = null, LockFile? resolvedGraph = null)
    {
        Check(project);
        if (removing.Count == 0 || removing.Any(dependency => !project.DirectDependencies.Contains(dependency)))
            throw new InvalidOperationException("The dependencies changed.");
        var folder = System.IO.Path.GetDirectoryName(project.FullPath.ToOSPath())!;
        var path = System.IO.Path.Combine(folder, "obj", LockFileFormat.AssetsFileName);
        if (resolvedGraph == null && !File.Exists(path)) throw new InvalidOperationException("The resolved dependency graph is unavailable.");
        var graph = resolvedGraph ?? new LockFileFormat().Read(path);
        if (graph.Targets.Count == 0) throw new InvalidOperationException("The resolved dependency graph has no target.");
        var frameworkInfos = evaluation is { Framework.Length: > 0 }
            ? graph.PackageSpec.TargetFrameworks.Where(info => string.Equals(info.TargetAlias, evaluation.Framework, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.FrameworkName.GetShortFolderName(), evaluation.Framework, StringComparison.OrdinalIgnoreCase)).ToArray()
            : graph.PackageSpec.TargetFrameworks.ToArray();
        var targets = graph.Targets.Where(target => frameworkInfos.Any(info => info.FrameworkName.Equals(target.TargetFramework))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("The evaluated target framework has not been restored.");
        IEnumerable<string> Root(DependencyRange dependency)
        {
            if (dependency.Type == DependencyType.Package) return [dependency.Name];
            var fullPath = System.IO.Path.GetFullPath(dependency.MSBuildProject, folder);
            return graph.Libraries.Where(library => library.Type == "project" && !string.IsNullOrEmpty(library.MSBuildProject)
                && string.Equals(System.IO.Path.GetFullPath(library.MSBuildProject, folder), fullPath, StringComparison.OrdinalIgnoreCase)).Select(library => library.Name);
        }
        HashSet<string> Reach(IEnumerable<string> roots, LockFileTarget target)
        {
            var libraries = target.Libraries.ToDictionary(library => library.Name, StringComparer.OrdinalIgnoreCase);
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(roots);
            while (pending.TryPop(out var name))
                if (reachable.Add(name) && libraries.TryGetValue(name, out var library))
                    foreach (var child in library.Dependencies) pending.Push(child.Id);
            return reachable;
        }
        var implicitRoots = frameworkInfos.SelectMany(framework => framework.Dependencies)
            .Where(dependency => dependency.AutoReferenced).Select(dependency => dependency.Name).ToArray();
        var roots = evaluation == null ? project.DirectDependencies.SelectMany(Root).ToArray()
            : evaluation.PackageReferences.Concat(evaluation.ProjectReferences.SelectMany(reference => Root(new DependencyRange("", null!, DependencyType.Project) { MSBuildProject = reference }))).ToArray();
        var removedRoots = removing.SelectMany(Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lost = targets.SelectMany(target => Reach(roots.Concat(implicitRoots), target)
            .Except(Reach(roots.Where(root => !removedRoots.Contains(root)).Concat(implicitRoots), target), StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in targets.SelectMany(target => target.Libraries).Where(library => lost.Contains(library.Name)))
        {
            var description = graph.Libraries.SingleOrDefault(description => description.Name == library.Name && description.Version == library.Version);
            if (description == null) continue;
            if (description.Type == "project")
            {
                if (!string.IsNullOrEmpty(description.MSBuildProject)) projects.Add(System.IO.Path.GetFullPath(description.MSBuildProject, folder));
                continue;
            }
            if (description.Type != "package" || string.IsNullOrEmpty(description.Path)) continue;
            foreach (var packageFolder in graph.PackageFolders)
            {
                var installed = System.IO.Path.Combine(packageFolder.Path, description.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                foreach (var item in library.CompileTimeAssemblies.Concat(library.RuntimeAssemblies).Concat(library.RuntimeTargets))
                    if (!item.Path.EndsWith("_._", StringComparison.Ordinal)) assemblies.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(installed, item.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))));
            }
        }
        foreach (var dependency in project.FlattenedDependencies.Where(dependency => lost.Contains(dependency.Name)))
        {
            foreach (var assembly in dependency.Assemblies) assemblies.Add(System.IO.Path.GetFullPath(assembly, folder));
            if (dependency.Package?.Container is SolutionProject source && !string.IsNullOrEmpty(source.TargetPath))
                assemblies.Add(System.IO.Path.GetFullPath(source.TargetPath, System.IO.Path.GetDirectoryName(source.FullPath.ToOSPath())!));
        }
        var actual = workspace.Session.Packages.SelectMany(package => package.LoadedAssemblies)
            .Where(loaded => loaded.Assembly != null && (assemblies.Contains(System.IO.Path.GetFullPath(loaded.Path))
                || projects.Any(projectPath => loaded.ProjectReference?.Location != null && string.Equals(System.IO.Path.GetFullPath(loaded.ProjectReference.Location.ToOSPath(), folder), projectPath, StringComparison.OrdinalIgnoreCase))))
            .Select(loaded => loaded.Assembly).Distinct().ToArray();
        return new(assemblies.ToArray(), projects.ToArray(), actual);
    }
    public void ValidateAuthoredTypeUsage(PackageContainer project, PackageDependencyRemovalScope scope, IReadOnlySet<AssetId>? deletingAssets = null)
    {
        if (scope.LoadedAssemblies.Length == 0) return;
        var visitor = new DependencyTypeVisitor(scope.LoadedAssemblies.ToHashSet());
        var authored = getAuthoredAssets();
        foreach (var item in project.Package.Assets)
            if (deletingAssets?.Contains(item.Id) != true) visitor.Visit(authored.GetValueOrDefault(item.Id) ?? item.Asset);
    }
    private sealed class DependencyTypeVisitor(HashSet<Assembly> assemblies) : AssetVisitorBase
    {
        private readonly HashSet<Type> inspected = [];
        private void CheckType(Type? type)
        {
            if (type == null || !inspected.Add(type)) return;
            if (assemblies.Contains(type.Assembly)) throw new InvalidOperationException("This dependency defines a type used by an asset: " + type.FullName);
            if (type.HasElementType) CheckType(type.GetElementType());
            if (type.IsGenericType) foreach (var argument in type.GetGenericArguments()) CheckType(argument);
            CheckType(type.BaseType);
            foreach (var implemented in type.GetInterfaces()) CheckType(implemented);
        }
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            CheckType(obj as Type ?? obj.GetType()); base.VisitObject(obj, descriptor, visitMembers);
        }
        public override void VisitPrimitive(object primitive, PrimitiveDescriptor descriptor)
        {
            CheckType(primitive as Type ?? primitive.GetType()); base.VisitPrimitive(primitive, descriptor);
        }
        public override void VisitObjectMember(object container, ObjectDescriptor descriptor, IMemberDescriptor member, object? value)
        {
            CheckType(member.Type); base.VisitObjectMember(container, descriptor, member, value);
        }
    }
}
