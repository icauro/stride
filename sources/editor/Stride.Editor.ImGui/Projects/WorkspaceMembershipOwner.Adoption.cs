using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Solutions;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class WorkspaceMembershipOwner
{
    private readonly List<AdoptedProjects> imports = [];
    internal void DisposeAdoptions()
    { foreach (var import in imports.ToArray()) { import.Dispose(); imports.Remove(import); } }
    internal void Adopt(PreparedSolutionProjects prepared)
    {
        if (!IsCurrent) throw new InvalidOperationException("The canonical session membership owner changed.");
        var owner = history.AuthoringState!;
        var import = new AdoptedProjects(this, prepared);
        prepared.IsRetained = true;
        var published = false;
        try
        {
            using var transaction = history.CreateTransaction("Import project '" + System.IO.Path.GetFileNameWithoutExtension(prepared.SourcePath) + "'");
            void Apply()
            {
                import.Forward();
                history.Record(new MembershipOperation(import), import.States.ToArray());
            }
            if (SessionHistory!.IsCapturingOperations) Apply();
            else { using var capture = SessionHistory.CaptureOperationsAtomic(Apply); capture.Publish(SessionHistory, "Import project '" + System.IO.Path.GetFileNameWithoutExtension(prepared.SourcePath) + "'"); }
            published = true; imports.Add(import);
        }
        catch (Exception failure)
        {
            if (!published)
            {
                try { import.RestoreInitial(); import.Dispose(); }
                catch (Exception recovery)
                {
                    imports.Add(import);
                    SessionHistory!.RetainFailedInitialOperation(new MembershipOperation(import), [owner, .. import.States]);
                    throw new AggregateException("Project adoption and restoration failed.", failure, recovery);
                }
            }
            throw;
        }
    }
    private sealed class MembershipOperation(AdoptedProjects import) : Operation
    {
        protected override void Rollback() => import.Backward();
        protected override void Rollforward() => import.Forward();
    }
    private sealed class AdoptedProjects : IDisposable
    {
        private readonly WorkspaceMembershipOwner owner;
        private readonly PreparedSolutionProjects prepared;
        private readonly PackageContainer[] before, added;
        private PackageContainer[]? after;
        private readonly Project[] folders;
        private readonly Dictionary<EditorAuthoringState, IDisposable> leases = [];
        private readonly List<(LoadedAssembly Original, LoadedAssembly Adopted)> assemblies = [];
        private readonly Dictionary<System.Reflection.Assembly, AssemblyRelease> assemblyReleases = new(ReferenceEqualityComparer.Instance);
        private sealed class AssemblyRelease(System.Reflection.Assembly assembly)
        {
            internal readonly AssemblyRegistry.Unregistration Registration = AssemblyRegistry.PrepareUnregister(assembly);
            internal bool RegistrationReleased, SerializerReleased, ContainerReleased;
        }
        private readonly HashSet<AssetId> assetIds;
        private readonly HashSet<string> authoredPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly long beforeRevision, afterRevision;
        private bool applied, everApplied, disposed;
        internal IEnumerable<EditorAuthoringState> States => leases.Keys;
        private PackageSession Live => owner.Workspace.Session;
        private EditorAuthoringRegistry Registry => owner.SessionHistory!.Registry;
        internal AdoptedProjects(WorkspaceMembershipOwner owner, PreparedSolutionProjects prepared)
        {
            this.owner = owner; this.prepared = prepared; before = Live.Projects.ToArray();
            var existing = Live.Packages.ToArray(); var reused = new Dictionary<Package, Package>(); var candidates = new List<PackageContainer>();
            foreach (var project in prepared.Projects)
            {
                var package = project.Package;
                var matches = existing.Where(current => SamePath(current.FullPath.ToOSPath(), package.FullPath.ToOSPath())
                    || !string.IsNullOrEmpty(package.Meta.Name) && string.Equals(current.Meta.Name, package.Meta.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 0)
                {
                    if (matches.Length != 1 || !string.Equals(matches[0].Meta.Name, package.Meta.Name, StringComparison.OrdinalIgnoreCase)
                        || !SamePath(matches[0].FullPath.ToOSPath(), package.FullPath.ToOSPath())
                        || !Equals(matches[0].Meta.Version, package.Meta.Version)) throw new InvalidOperationException("A package identity or version conflicts with the current solution.");
                    reused.Add(package, matches[0]); continue;
                }
                if (existing.Any(current => string.Equals(current.Meta.Name, package.Meta.Name, StringComparison.OrdinalIgnoreCase)
                    && current.Meta.Version != null && package.Meta.Version != null && !Equals(current.Meta.Version, package.Meta.Version)))
                    throw new InvalidOperationException("A dependency version conflicts with the current solution.");
                if (package.Assets.Any(item => existing.SelectMany(current => current.Assets).Any(current => current.Id == item.Id)))
                    throw new InvalidOperationException("An imported asset identity is already owned by another package.");
                if (project is SolutionProject solutionProject && (Live.AuthoringSolution.Projects.Contains(solutionProject.Id)
                    || before.OfType<SolutionProject>().Any(current => SamePath(current.FullPath.ToOSPath(), solutionProject.FullPath.ToOSPath()))))
                    throw new InvalidOperationException("The solution already contains this project.");
                candidates.Add(project);
            }
            added = candidates.ToArray();
            if (!added.OfType<SolutionProject>().Any(project => !project.Package.IsReadOnly)) throw new InvalidOperationException("The solution already contains the selected project.");
            foreach (var project in added)
                foreach (var dependency in project.FlattenedDependencies)
                    if (dependency.Package != null && reused.TryGetValue(dependency.Package, out var canonical)) dependency.Package = canonical;
            var folderIds = added.OfType<SolutionProject>().Select(project => project.VSProject.ParentGuid).Where(id => id != Guid.Empty).ToHashSet();
            var nativeFolders = prepared.Session.AuthoringSolution.Projects.Where(project => project.IsSolutionFolder).ToArray();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var folder in nativeFolders.Where(folder => folderIds.Contains(folder.Guid)))
                    if (folder.ParentGuid != Guid.Empty && folderIds.Add(folder.ParentGuid)) changed = true;
            }
            folders = nativeFolders.Where(folder => folderIds.Contains(folder.Guid)).ToArray();
            if (folders.Any(folder => Live.AuthoringSolution.Projects.Contains(folder.Guid))) throw new InvalidOperationException("A solution folder identity conflicts with the current solution.");
            var liveAssemblies = Live.AssemblyContainer.LoadedAssemblies;
            foreach (var assembly in prepared.Assemblies)
                if (liveAssemblies.Any(current => !ReferenceEquals(current.Assembly, assembly.Assembly)
                    && string.Equals(current.Assembly.GetName().Name, assembly.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("An imported assembly conflicts with a live project assembly. Reload the complete solution together.");
            assetIds = added.SelectMany(project => project.Package.Assets).Select(item => item.Id).ToHashSet();
            foreach (var project in added)
            {
                authoredPaths.Add(System.IO.Path.GetFullPath(project.Package.FullPath.ToOSPath()));
                if (project is SolutionProject solutionProject) authoredPaths.Add(System.IO.Path.GetFullPath(solutionProject.FullPath.ToOSPath()));
                foreach (var item in project.Package.Assets) authoredPaths.Add(System.IO.Path.GetFullPath(item.FullPath.ToOSPath()));
            }
            beforeRevision = owner.Revision; afterRevision = owner.AllocateRevision();
        }
        private static bool SamePath(string left, string right) => string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        private void Pin(EditorAuthoringState state) { if (!leases.ContainsKey(state)) leases.Add(state, state.AcquireHistory()); }
        private void CaptureCurrentStates()
        {
            foreach (var state in Registry.PublishedStates.Where(state => state.AssetId is { } id && assetIds.Contains(id) || authoredPaths.Contains(state.CanonicalPath)).ToArray()) Pin(state);
        }
        private void AdoptAssemblies()
        {
            if (assemblies.Count > 0) return;
            foreach (var original in prepared.Assemblies)
            {
                if (Live.AssemblyContainer.LoadedAssemblies.Any(entry => ReferenceEquals(entry.Assembly, original.Assembly))) continue;
                // Rehome the native resolver record as well as the project models;
                // LoadedAssembly.Container must point at the live resolver.
                var adopted = new LoadedAssembly(Live.AssemblyContainer, original.Path, original.Assembly, original.Dependencies, original.ArtifactLifetime);
                try
                {
                    prepared.Session.AssemblyContainer.RemoveAssembly(original.Assembly);
                    Live.AssemblyContainer.RestoreAssembly(adopted); assemblies.Add((original, adopted));
                }
                catch { Live.AssemblyContainer.RemoveAssembly(adopted.Assembly); prepared.Session.AssemblyContainer.RestoreAssembly(original); throw; }
            }
        }
        internal void Forward()
        {
            if (applied) return;
            if (owner.Revision != beforeRevision || !Live.Projects.SequenceEqual(before)) throw new InvalidOperationException("The solution changed after project preparation.");
            foreach (var state in leases.Keys)
                if (Registry.Find(state.CanonicalPath) is { } occupied && !ReferenceEquals(occupied, state)) throw new InvalidOperationException("An imported document path has another canonical owner.");
            var attached = new List<PackageContainer>(); var attachedFolders = new List<Project>();
            try
            {
                AdoptAssemblies();
                foreach (var folder in folders) { Live.AuthoringSolution.Projects.Add(folder); attachedFolders.Add(folder); }
                foreach (var project in added)
                {
                    if (!ReferenceEquals(project.Session, prepared.Session)) throw new InvalidOperationException("The prepared project owner changed.");
                    prepared.Session.Projects.Remove(project);
                    try { Live.Projects.Add(project); attached.Add(project); }
                    catch { if (ReferenceEquals(project.Session, Live)) Live.Projects.Remove(project); prepared.Session.Projects.Add(project); throw; }
                }
                if (!everApplied)
                {
                    foreach (var project in added)
                    {
                        Pin(Registry.GetOrCreate(project.Package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(owner.Workspace, project.Package, owner.SessionHistory!)));
                        if (project is SolutionProject solutionProject)
                            Pin(Registry.GetOrCreate(solutionProject.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(solutionProject, owner.SessionHistory!)));
                    }
                }
                else foreach (var state in leases.Keys) Registry.Publish(state);
                owner.Workspace.RefreshAssets();
                owner.SessionHistory!.HierarchyGraphs.RefreshPublished(); owner.SessionHistory.AssetGraphs.RefreshPublished(); owner.SessionHistory.UiLibraryGraphs.RefreshPublished();
                CaptureCurrentStates();
                after = Live.Projects.ToArray(); owner.Revision = afterRevision; Live.IsDirty = true; applied = everApplied = true;
            }
            catch
            {
                CaptureCurrentStates();
                foreach (var state in leases.Keys.Where(state => state.IsPublished)) Registry.Unpublish(state);
                foreach (var project in attached.AsEnumerable().Reverse()) { Live.Projects.Remove(project); prepared.Session.Projects.Add(project); }
                foreach (var folder in attachedFolders.AsEnumerable().Reverse()) Live.AuthoringSolution.Projects.Remove(folder);
                owner.Revision = beforeRevision; owner.Workspace.RefreshAssets(); throw;
            }
        }
        internal void Backward()
        {
            if (!applied) return;
            if (owner.Revision != afterRevision || after == null || !Live.Projects.SequenceEqual(after)) throw new InvalidOperationException("The imported solution membership changed.");
            if (before.Any(project => project.FlattenedDependencies.Any(dependency => dependency.Package != null && added.Any(addedProject => ReferenceEquals(addedProject.Package, dependency.Package)))))
                throw new InvalidOperationException("Another project still depends on the imported project.");
            CaptureCurrentStates();
            var detached = new List<PackageContainer>(); var detachedFolders = new List<Project>();
            try
            {
                foreach (var state in leases.Keys.Where(state => state.IsPublished)) Registry.Unpublish(state);
                foreach (var project in added.Reverse()) { Live.Projects.Remove(project); prepared.Session.Projects.Add(project); detached.Add(project); }
                foreach (var folder in folders.Reverse()) { Live.AuthoringSolution.Projects.Remove(folder); detachedFolders.Add(folder); }
                owner.Revision = beforeRevision; Live.IsDirty = true; applied = false; owner.Workspace.RefreshAssets();
            }
            catch
            {
                foreach (var folder in detachedFolders.AsEnumerable().Reverse()) Live.AuthoringSolution.Projects.Add(folder);
                foreach (var project in detached.AsEnumerable().Reverse()) { prepared.Session.Projects.Remove(project); Live.Projects.Add(project); }
                foreach (var state in leases.Keys) Registry.Publish(state);
                owner.Revision = afterRevision; owner.Workspace.RefreshAssets(); throw;
            }
        }
        internal void RestoreInitial() { if (applied) Backward(); }
        internal void ReleasePins()
        {
            foreach (var pair in leases.ToArray()) { pair.Value.Dispose(); leases.Remove(pair.Key); }
        }
        public void Dispose()
        {
            if (disposed) return;
            ReleasePins();
            // The live session owns adopted assemblies. When an import is undone,
            // its retained preparation must not unregister those same registrations.
            foreach (var package in prepared.Session.Packages)
                package.LoadedAssemblies.RemoveAll(entry => assemblies.Any(pair => ReferenceEquals(pair.Adopted.Assembly, entry.Assembly)));
            foreach (var pair in assemblies)
            {
                var assembly = pair.Adopted.Assembly;
                if (Live.Packages.SelectMany(package => package.LoadedAssemblies).Any(entry => ReferenceEquals(entry.Assembly, assembly)))
                {
                    if (assemblyReleases.ContainsKey(assembly))
                        throw new InvalidOperationException("An imported assembly was reused while its cleanup was incomplete.");
                    continue;
                }
                if (!assemblyReleases.TryGetValue(assembly, out var release))
                    assemblyReleases.Add(assembly, release = new AssemblyRelease(assembly));
                if (!release.RegistrationReleased) { release.Registration.Complete(); release.RegistrationReleased = true; }
                if (!release.SerializerReleased) { DataSerializerFactory.UnregisterSerializationAssembly(assembly); release.SerializerReleased = true; }
                if (!release.ContainerReleased) { Live.AssemblyContainer.UnloadAssembly(assembly); release.ContainerReleased = true; }
            }
            prepared.Dispose();
            disposed = true;
        }
    }
}
