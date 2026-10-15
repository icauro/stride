using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Retains the actual isolated package closure across native session membership undo.</summary>
internal sealed class DependencyPackageAdoption : Operation
{
    private readonly ProjectWorkspace workspace;
    private readonly EditorSessionHistory history;
    private readonly PreparedSolutionProjects prepared;
    private readonly PackageContainer[] before, added;
    private readonly List<EditorAuthoringState> states = [];
    private readonly List<EditorAuthoringState> replacedStates = [];
    private readonly Dictionary<PackageContainer, PackageContainer> replacements = [];
    private readonly List<(Dependency Dependency, Package Before, Package After)> replacedReferences = [];
    private readonly List<IDisposable> leases = [];
    private readonly List<(LoadedAssembly Original, LoadedAssembly Adopted)> assemblies = [];
    private PackageContainer[]? after;
    private bool applied;
    private bool replacedPinned;
    private bool initialRecovery;
    internal IReadOnlyList<EditorAuthoringState> States => [.. states, .. replacedStates];
    internal DependencyPackageAdoption(ProjectWorkspace workspace, EditorSessionHistory history, PreparedSolutionProjects prepared)
    {
        this.workspace = workspace; this.history = history; this.prepared = prepared;
        before = workspace.Session.Projects.ToArray();
        var candidates = new List<PackageContainer>();
        var reused = new Dictionary<Package, Package>();
        foreach (var container in prepared.Projects)
        {
            var package = container.Package;
            if (package.State < PackageState.AssetsReady || !package.IsReadOnly || container is SolutionProject)
                throw new InvalidOperationException("Only a loaded native store-library closure can be adopted by dependency restore.");
            var originalPath = prepared.OriginalPackagePath(package);
            var matches = workspace.Session.Packages.Where(current => string.Equals(current.FullPath.ToOSPath(), originalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Meta.Name, package.Meta.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 0)
            {
                if (matches.Length != 1 || !string.Equals(matches[0].FullPath.ToOSPath(), originalPath, StringComparison.OrdinalIgnoreCase) || !Equals(matches[0].Meta.Version, package.Meta.Version))
                    throw new InvalidOperationException("A dependency package identity or version conflicts with the current session.");
                if (matches[0].State >= PackageState.AssetsReady) { reused.Add(package, matches[0]); continue; }
                var oldPackage = matches[0];
                if (oldPackage.Container is SolutionProject || oldPackage.IsDirty
                    || history.Registry.PublishedStates.Any(state => state.AssetId is { } id && oldPackage.Assets.Any(item => item.Id == id)))
                    throw new InvalidOperationException("The unloaded package has retained authored asset owners and cannot be replaced by library acquisition.");
                if (history.Registry.Find(oldPackage.FullPath.ToOSPath()) is { } oldState)
                {
                    if (oldState.Document is not PackageMetadataDocument || oldState.Document.IsDirty)
                        throw new InvalidOperationException("The unloaded package's canonical metadata owner has authored changes.");
                    replacedStates.Add(oldState);
                }
                replacements.Add(container, oldPackage.Container);
                foreach (var dependency in before.SelectMany(project => project.FlattenedDependencies).Where(dependency => ReferenceEquals(dependency.Package, oldPackage)))
                    replacedReferences.Add((dependency, oldPackage, package));
            }
            if (package.Assets.Any(item => workspace.Session.Packages.Where(current => !replacements.Values.Any(container => ReferenceEquals(container.Package, current)))
                    .SelectMany(current => current.Assets).Any(current => current.Id == item.Id)))
                throw new InvalidOperationException("A dependency asset identity is already owned by the current session.");
            candidates.Add(container);
        }
        added = candidates.ToArray();
        foreach (var project in added)
            foreach (var dependency in project.FlattenedDependencies)
                if (dependency.Package != null && reused.TryGetValue(dependency.Package, out var canonical)) dependency.Package = canonical;
        foreach (var assembly in prepared.Assemblies)
            if (workspace.Session.AssemblyContainer.LoadedAssemblies.Any(current => !ReferenceEquals(current.Assembly, assembly.Assembly)
                && string.Equals(current.Assembly.GetName().Name, assembly.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A dependency assembly conflicts with a retained session assembly generation.");
    }
    internal void Apply()
    {
        initialRecovery = true;
        Rollforward();
        initialRecovery = false;
    }
    internal void Restore() => Rollback();
    private void Refresh()
    {
        workspace.RefreshAssets(); history.HierarchyGraphs.RefreshPublished(); history.AssetGraphs.RefreshPublished(); history.UiLibraryGraphs.RefreshPublished();
    }
    protected override void Rollforward()
    {
        if (applied) return;
        if (!workspace.Session.Projects.SequenceEqual(before)) throw new InvalidOperationException("The package session changed after dependency preparation.");
        var attached = new List<PackageContainer>();
        var removed = new List<PackageContainer>();
        try
        {
            if (assemblies.Count == 0)
                foreach (var original in prepared.Assemblies)
                {
                    if (workspace.Session.AssemblyContainer.LoadedAssemblies.Any(current => ReferenceEquals(current.Assembly, original.Assembly))) continue;
                    var adopted = new LoadedAssembly(workspace.Session.AssemblyContainer, original.Path, original.Assembly, original.Dependencies, original.ArtifactLifetime);
                    prepared.Session.AssemblyContainer.RemoveAssembly(original.Assembly);
                    try { workspace.Session.AssemblyContainer.RestoreAssembly(adopted); assemblies.Add((original, adopted)); }
                    catch { prepared.Session.AssemblyContainer.RestoreAssembly(original); throw; }
                }
            foreach (var state in replacedStates)
            {
                if (!replacedPinned) leases.Add(state.AcquireHistory());
                if (state.IsPublished) history.Registry.Unpublish(state);
            }
            replacedPinned = true;
            foreach (var container in replacements.Values) { workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); removed.Add(container); }
            foreach (var container in added)
            {
                prepared.Session.Projects.Remove(container);
                try
                {
                    if (replacements.TryGetValue(container, out var old)) workspace.Session.Projects.Insert(Math.Min(Array.IndexOf(before, old), workspace.Session.Projects.Count), container);
                    else workspace.Session.Projects.Add(container);
                    attached.Add(container);
                }
                catch { if (ReferenceEquals(container.Session, workspace.Session)) workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); throw; }
            }
            if (states.Count == 0)
                foreach (var container in added)
                {
                    var state = history.Registry.GetOrCreate(container.Package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(workspace, container.Package, history));
                    states.Add(state); leases.Add(state.AcquireHistory());
                }
            else foreach (var state in states) history.Registry.Publish(state);
            foreach (var reference in replacedReferences) reference.Dependency.Package = reference.After;
            after = workspace.Session.Projects.ToArray(); applied = true; Refresh();
        }
        catch
        {
            foreach (var state in states.Where(state => state.IsPublished)) history.Registry.Unpublish(state);
            foreach (var reference in replacedReferences) reference.Dependency.Package = reference.Before;
            foreach (var container in attached.AsEnumerable().Reverse()) { workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); }
            foreach (var container in removed.OrderBy(container => Array.IndexOf(before, container)))
            { prepared.Session.Projects.Remove(container); workspace.Session.Projects.Insert(Math.Min(Array.IndexOf(before, container), workspace.Session.Projects.Count), container); }
            foreach (var state in replacedStates) history.Registry.Publish(state);
            applied = false; workspace.RefreshAssets(); throw;
        }
    }
    protected override void Rollback()
    {
        if (initialRecovery)
        {
            foreach (var state in states.Where(state => state.IsPublished)) history.Registry.Unpublish(state);
            foreach (var reference in replacedReferences) reference.Dependency.Package = reference.Before;
            foreach (var container in added.Reverse().Where(container => ReferenceEquals(container.Session, workspace.Session)))
            { workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); }
            foreach (var container in replacements.Values.OrderBy(container => Array.IndexOf(before, container)))
                if (ReferenceEquals(container.Session, prepared.Session))
                { prepared.Session.Projects.Remove(container); workspace.Session.Projects.Insert(Math.Min(Array.IndexOf(before, container), workspace.Session.Projects.Count), container); }
            foreach (var state in replacedStates) history.Registry.Publish(state);
            applied = false; Refresh(); initialRecovery = false; return;
        }
        if (!applied) return;
        if (after == null || !workspace.Session.Projects.SequenceEqual(after)) throw new InvalidOperationException("The adopted package membership changed.");
        if (before.Any(project => project.FlattenedDependencies.Any(dependency => dependency.Package != null
            && added.Any(container => ReferenceEquals(container.Package, dependency.Package))
            && !replacedReferences.Any(reference => ReferenceEquals(reference.Dependency, dependency) && ReferenceEquals(reference.After, dependency.Package)))))
            throw new InvalidOperationException("A project still refers to the adopted dependency package.");
        var detached = new List<PackageContainer>();
        try
        {
            foreach (var state in states.Where(state => state.IsPublished)) history.Registry.Unpublish(state);
            foreach (var reference in replacedReferences) reference.Dependency.Package = reference.Before;
            foreach (var container in added.Reverse()) { workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); detached.Add(container); }
            foreach (var container in replacements.Values.OrderBy(container => Array.IndexOf(before, container)))
            { prepared.Session.Projects.Remove(container); workspace.Session.Projects.Insert(Math.Min(Array.IndexOf(before, container), workspace.Session.Projects.Count), container); }
            foreach (var state in replacedStates) history.Registry.Publish(state);
            applied = false; Refresh();
        }
        catch
        {
            foreach (var state in replacedStates.Where(state => state.IsPublished)) history.Registry.Unpublish(state);
            foreach (var container in replacements.Values.Where(container => ReferenceEquals(container.Session, workspace.Session)))
            { workspace.Session.Projects.Remove(container); prepared.Session.Projects.Add(container); }
            foreach (var container in detached.AsEnumerable().Reverse())
            {
                prepared.Session.Projects.Remove(container);
                if (replacements.TryGetValue(container, out var old)) workspace.Session.Projects.Insert(Math.Min(Array.IndexOf(before, old), workspace.Session.Projects.Count), container);
                else workspace.Session.Projects.Add(container);
            }
            foreach (var state in states) history.Registry.Publish(state);
            foreach (var reference in replacedReferences) reference.Dependency.Package = reference.After;
            applied = true; workspace.RefreshAssets(); throw;
        }
    }
    internal void Release()
    {
        foreach (var lease in leases) lease.Dispose(); leases.Clear();
        foreach (var package in prepared.Session.Packages)
            package.LoadedAssemblies.RemoveAll(entry => assemblies.Any(pair => ReferenceEquals(pair.Adopted.Assembly, entry.Assembly)));
        if (!applied)
            foreach (var pair in assemblies)
                if (!workspace.Session.Packages.SelectMany(package => package.LoadedAssemblies).Any(entry => ReferenceEquals(entry.Assembly, pair.Adopted.Assembly)))
                {
                    AssemblyRegistry.Unregister(pair.Adopted.Assembly); DataSerializerFactory.UnregisterSerializationAssembly(pair.Adopted.Assembly);
                    workspace.Session.AssemblyContainer.UnloadAssembly(pair.Adopted.Assembly);
                }
        if (applied) workspace.Session.RetainLifetime(prepared);
        else prepared.Dispose();
    }
    protected override void FreezeContent() => Release();
}
