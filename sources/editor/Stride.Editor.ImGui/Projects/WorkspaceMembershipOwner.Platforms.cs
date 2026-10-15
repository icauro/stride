using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class WorkspaceMembershipOwner
{
    private readonly List<PlatformReplacementAuthority> platformReplacements = [];

    // The caller owns the surrounding native capture, published file receipts and
    // retired activation. Incoming declarations are permitted only by this exact
    // detached native project mapping; ordinary Remove retains its original guard.
    internal void ReplacePlatforms(PackageContainer[] removing, PreparedSolutionProjects prepared,
        Action refreshed, Action<EditorAuthoringState[], bool> synchronizeViews)
    {
        if (!IsCurrent || !SessionHistory.IsCapturingOperations)
            throw new InvalidOperationException("Platform replacement requires the current native publication capture.");
        var replacement = new PlatformReplacementAuthority(this, removing, prepared);
        platformReplacements.Add(replacement);
        if (removing.Length != 0) Remove(removing, refreshed, synchronizeViews, replacement);
        if (prepared.Projects.Any(project => !ownerContains(project))) Adopt(prepared);
        bool ownerContains(PackageContainer project) => Workspace.Session.Projects.Any(current =>
            string.Equals(current.Package.FullPath.ToOSPath(), project.Package.FullPath.ToOSPath(), StringComparison.OrdinalIgnoreCase));
        replacement.PinPublishedTargets();
        history.Record(new PlatformDependencyOperation(replacement), replacement.States);
        replacement.Change(true);
    }
    private sealed class PlatformDependencyOperation(PlatformReplacementAuthority replacement) : Operation
    {
        protected override void Rollback() => replacement.Change(false);
        protected override void Rollforward() => replacement.Change(true);
    }
    private sealed class PlatformReplacementAuthority
    {
        private readonly WorkspaceMembershipOwner owner;
        private readonly Dictionary<PackageContainer, SolutionProject> replacements = [];
        private readonly Dictionary<EditorAuthoringState, IDisposable> leases = [];
        private readonly DirectCertificate[] declarations;
        private readonly ResolvedCertificate[] resolved;
        private readonly AssemblyCertificate[] assemblies;
        private readonly (Dependency Item, string Name, PackageVersion Version, DependencyType Type, string Project, Package Package, string[] Assemblies)[] dependencyValues;
        private readonly (PackageLoadedAssembly Item, string Path, System.Reflection.Assembly Assembly)[] assemblyValues;
        private readonly Guid? detachedStartup, replacedStartup;
        private bool applied;
        private bool? recoveryGoal;
        internal EditorAuthoringState[] States => leases.Keys.ToArray();
        private sealed record DirectCertificate(PackageContainer Owner, DependencyRange[] Items,
            (string Name, PackageVersionRange Version, DependencyType Type, string Project)[] Values);
        private sealed record ResolvedCertificate(PackageContainer Owner, Dependency[] Original, Dependency[] Detached, Dependency[] Replaced);
        private sealed record AssemblyCertificate(Package Old, Package Next, PackageLoadedAssembly[] Original, PackageLoadedAssembly[] Target);
        internal PlatformReplacementAuthority(WorkspaceMembershipOwner owner, PackageContainer[] removing, PreparedSolutionProjects prepared)
        {
            this.owner = owner;
            var live = owner.Workspace.Session;
            foreach (var old in removing.OfType<SolutionProject>())
            {
                var matches = prepared.Projects.OfType<SolutionProject>().Where(next => SamePath(next.FullPath.ToOSPath(), old.FullPath.ToOSPath())).ToArray();
                if (matches.Length == 0) continue; // A genuinely removed platform still requires native dependency removal.
                if (matches.Length != 1) throw new InvalidOperationException("A generated platform has ambiguous native project ownership.");
                var next = matches[0];
                if (old.Package.IsReadOnly || next.Package.IsReadOnly || !ReferenceEquals(old.Session, live)
                    || !ReferenceEquals(next.Session, prepared.Session) || next.Package.State < PackageState.AssetsReady
                    || next.Type != old.Type || next.Platform != old.Platform
                    || !SamePath(next.Package.FullPath.ToOSPath(), old.Package.FullPath.ToOSPath())
                    || next.Package.Meta.Name != old.Package.Meta.Name || !Equals(next.Package.Meta.Version, old.Package.Meta.Version))
                    throw new InvalidOperationException("The generated platform does not replace the same native project/package identity.");
                // Native references keep their actual IDs/type/URL. A template which
                // removed an authored asset cannot silently satisfy its incoming links.
                foreach (var item in old.Package.Assets)
                    if (!next.Package.Assets.Any(candidate => candidate.Id == item.Id && candidate.Location == item.Location
                        && candidate.Asset.GetType() == item.Asset.GetType()))
                        throw new InvalidOperationException("The generated platform does not preserve an existing authored asset identity: " + item.Location);
                replacements.Add(old, next);
            }
            var startup = live.AuthoringSolution.StartupProjectGuid;
            detachedStartup = startup is { } selectedStartup && removing.OfType<SolutionProject>().Any(project => project.Id == selectedStartup) ? null : startup;
            replacedStartup = startup is { } originalStartup && replacements.FirstOrDefault(pair => pair.Key is SolutionProject project && project.Id == originalStartup).Value is { } startupReplacement
                ? startupReplacement.Id : detachedStartup;
            var remaining = live.Projects.Except(removing).ToArray();
            declarations = remaining.Select(container => new DirectCertificate(container, container.DirectDependencies.ToArray(),
                container.DirectDependencies.Select(dependency => (dependency.Name, dependency.VersionRange, dependency.Type, dependency.MSBuildProject)).ToArray())).ToArray();
            resolved = remaining.Select(container =>
            {
                var original = container.FlattenedDependencies.ToArray();
                var detached = original.Where(dependency => dependency.Package == null
                    || !removing.Any(old => ReferenceEquals(old.Package, dependency.Package))).ToArray();
                var mapped = new List<Dependency>();
                foreach (var dependency in original)
                {
                    var old = replacements.Keys.FirstOrDefault(project => ReferenceEquals(project.Package, dependency.Package));
                    if (old == null) { if (detached.Contains(dependency)) mapped.Add(dependency); continue; }
                    var next = replacements[old];
                    var replacement = new Dependency(dependency.Name, dependency.Version, dependency.Type)
                    { Package = next.Package, MSBuildProject = dependency.MSBuildProject };
                    replacement.Assemblies.AddRange(dependency.Assemblies);
                    mapped.Add(replacement);
                }
                return new ResolvedCertificate(container, original, detached, mapped.ToArray());
            }).ToArray();
            assemblies = replacements.Select(pair => new AssemblyCertificate(pair.Key.Package, pair.Value.Package,
                pair.Key.Package.LoadedAssemblies.ToArray(), pair.Value.Package.LoadedAssemblies.ToArray())).ToArray();
            dependencyValues = resolved.SelectMany(entry => entry.Original.Concat(entry.Replaced)).Distinct().Select(item =>
                (item, item.Name, item.Version, item.Type, item.MSBuildProject, item.Package, item.Assemblies.ToArray())).ToArray();
            assemblyValues = assemblies.SelectMany(entry => entry.Original.Concat(entry.Target)).Distinct().Select(item =>
                (item, item.Path, item.Assembly)).ToArray();
            if (assemblies.Any(entry => entry.Target.Any(loaded => entry.Original.Any(original => original.Assembly != null && loaded.Assembly != null && original.Assembly.GetName().Name == loaded.Assembly.GetName().Name
                && !ReferenceEquals(original.Assembly, loaded.Assembly)))))
                throw new InvalidOperationException("A generated platform introduced a conflicting live assembly owner.");
            try
            {
                Pin(owner.history.AuthoringState!);
                foreach (var state in owner.SessionHistory.Registry.PublishedStates.Where(state =>
                    remaining.Concat(removing).Any(container => SamePath(state.CanonicalPath, container.Package.FullPath.ToOSPath())
                        || container is SolutionProject project && SamePath(state.CanonicalPath, project.FullPath.ToOSPath()))).ToArray()) Pin(state);
            }
            catch { ReleasePins(); throw; }
        }
        private static bool SamePath(string left, string right) => string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        private void Pin(EditorAuthoringState state) { if (!leases.ContainsKey(state)) leases.Add(state, state.AcquireHistory()); }
        internal void PinPublishedTargets()
        {
            foreach (var state in owner.SessionHistory.Registry.PublishedStates.Where(state => replacements.Values.Any(project =>
                SamePath(state.CanonicalPath, project.FullPath.ToOSPath()) || SamePath(state.CanonicalPath, project.Package.FullPath.ToOSPath()))).ToArray()) Pin(state);
        }
        private void ValidateDeclarations()
        {
            foreach (var value in dependencyValues)
                if (value.Item.Name != value.Name || !Equals(value.Item.Version, value.Version) || value.Item.Type != value.Type
                    || value.Item.MSBuildProject != value.Project || !ReferenceEquals(value.Item.Package, value.Package)
                    || !value.Item.Assemblies.SequenceEqual(value.Assemblies))
                    throw new InvalidOperationException("A captured native resolved dependency changed.");
            foreach (var value in assemblyValues)
                if (value.Item.Path != value.Path || !ReferenceEquals(value.Item.Assembly, value.Assembly))
                    throw new InvalidOperationException("A captured native package assembly record changed.");
            foreach (var entry in declarations)
            {
                if (!owner.Workspace.Session.Projects.Contains(entry.Owner) || !entry.Owner.DirectDependencies.SequenceEqual(entry.Items))
                    throw new InvalidOperationException("A platform dependent's native declaration collection changed.");
                for (var index = 0; index < entry.Items.Length; index++)
                {
                    var actual = entry.Items[index]; var value = entry.Values[index];
                    if (actual.Name != value.Name || !Equals(actual.VersionRange, value.Version) || actual.Type != value.Type || actual.MSBuildProject != value.Project)
                        throw new InvalidOperationException("A platform dependent's native declaration changed.");
                }
            }
        }
        internal bool Allows(PackageContainer old, PackageContainer referencer, DependencyRange dependency)
        {
            ValidateDeclarations();
            return replacements.TryGetValue(old, out var next) && next.Package.State >= PackageState.AssetsReady
                && declarations.Any(entry => ReferenceEquals(entry.Owner, referencer) && entry.Items.Contains(dependency))
                && PackageDependencies.RefersTo(next.Package, dependency);
        }
        internal void Change(bool forward)
        {
            ValidateDeclarations();
            if (recoveryGoal is { } goal) { Apply(goal); applied = goal; recoveryGoal = null; }
            if (applied == forward) return;
            if (owner.Workspace.Session.AuthoringSolution.StartupProjectGuid != (forward ? detachedStartup : replacedStartup))
                throw new InvalidOperationException("The native startup project changed during platform publication.");
            foreach (var entry in resolved)
                if (!entry.Owner.FlattenedDependencies.SequenceEqual(forward ? entry.Detached : entry.Replaced))
                    throw new InvalidOperationException("A platform dependent's resolved native collection changed.");
            foreach (var entry in assemblies)
                if (!entry.Old.LoadedAssemblies.SequenceEqual(forward ? entry.Original : [])
                    || !entry.Next.LoadedAssemblies.SequenceEqual(forward ? entry.Target : entry.Target.Concat(entry.Original).Distinct()))
                    throw new InvalidOperationException("A platform assembly changed its canonical package owner.");
            try { Apply(forward); applied = forward; }
            catch (Exception failure)
            {
                try { Apply(!forward); }
                catch (Exception compensation)
                { recoveryGoal = !forward; throw new AggregateException("Native platform dependency replacement and restoration failed.", failure, compensation); }
                throw;
            }
        }
        private void Apply(bool forward)
        {
            foreach (var entry in resolved)
            {
                var target = forward ? entry.Replaced : entry.Detached;
                // Only exact native records from this journal are admissible during recovery.
                if (entry.Owner.FlattenedDependencies.Any(item => !entry.Detached.Contains(item) && !entry.Replaced.Contains(item)))
                    throw new InvalidOperationException("An external dependency conflicts with platform publication recovery.");
                entry.Owner.FlattenedDependencies.Clear(); foreach (var item in target) entry.Owner.FlattenedDependencies.Add(item);
            }
            foreach (var entry in assemblies)
            {
                if (entry.Old.LoadedAssemblies.Concat(entry.Next.LoadedAssemblies).Any(item => !entry.Original.Contains(item) && !entry.Target.Contains(item)))
                    throw new InvalidOperationException("An external assembly conflicts with platform publication recovery.");
                entry.Old.LoadedAssemblies.Clear(); if (!forward) entry.Old.LoadedAssemblies.AddRange(entry.Original);
                entry.Next.LoadedAssemblies.Clear(); entry.Next.LoadedAssemblies.AddRange(forward ? entry.Target.Concat(entry.Original).Distinct() : entry.Target);
            }
            var solution = owner.Workspace.Session.AuthoringSolution;
            if (solution.StartupProjectGuid != detachedStartup && solution.StartupProjectGuid != replacedStartup)
                throw new InvalidOperationException("An external startup project conflicts with platform publication recovery.");
            solution.StartupProjectGuid = forward ? replacedStartup : detachedStartup;
            owner.Workspace.Session.IsDirty = true;
        }
        internal void ReleasePins()
        { foreach (var pair in leases.ToArray()) { pair.Value.Dispose(); leases.Remove(pair.Key); } }
    }
}
