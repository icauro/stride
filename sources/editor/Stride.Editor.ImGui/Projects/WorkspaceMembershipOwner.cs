using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class WorkspaceMembershipOwner
{
    internal readonly ProjectWorkspace Workspace;
    internal readonly IEditorDocument Document;
    private readonly EditorDocumentHistoryScope history;
    private readonly Func<long> readRevision, allocateRevision;
    private readonly Action<long> restoreRevision;
    private readonly List<RemovedProjects> removals = [];
    internal EditorSessionHistory SessionHistory => history.SessionHistory!;
    internal long Revision { get => readRevision(); set => restoreRevision(value); }
    internal long AllocateRevision() => allocateRevision();
    internal bool IsCurrent => history.AuthoringState is { IsPublished: true, IsDisposed: false } state
        && ReferenceEquals(state.Registry.Find(Document.Path), state) && ReferenceEquals(state.Document, Document);
    internal WorkspaceMembershipOwner(ProjectWorkspace workspace, IEditorDocument document, EditorDocumentHistoryScope history,
        Func<long> readRevision, Func<long> allocateRevision, Action<long> restoreRevision)
    {
        Workspace = workspace; Document = document; this.history = history;
        this.readRevision = readRevision; this.allocateRevision = allocateRevision; this.restoreRevision = restoreRevision;
    }

    internal void Remove(PackageContainer[] selected, Action refreshed, Action<EditorAuthoringState[], bool> synchronizeViews)
        => Remove(selected, refreshed, synchronizeViews, null);

    private void Remove(PackageContainer[] selected, Action refreshed, Action<EditorAuthoringState[], bool> synchronizeViews,
        PlatformReplacementAuthority? replacements)
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The canonical session membership owner changed.");
        var removal = new RemovedProjects(this, selected, refreshed, synchronizeViews, replacements);
        removals.Add(removal);
        try
        {
            void Apply()
            {
                // Record first so real capture compensation owns every native phase.
                history.Record(new RemovalOperation(removal), removal.States);
                removal.Change(true);
            }
            if (SessionHistory.IsCapturingOperations) Apply();
            else
            {
                using var capture = SessionHistory.CaptureOperationsAtomic(Apply);
                capture.Publish(SessionHistory, selected.Length == 1 ? "Delete package '" + selected[0].Package.Meta.Name + "'" : "Delete packages/projects");
            }
        }
        catch
        {
            // Retain this exact owner through failed-capture compensation and teardown.
            throw;
        }
    }

    internal void ReleaseRetainedStates()
    {
        foreach (var removal in removals) removal.ReleaseForSessionTeardown();
        removals.Clear();
        foreach (var import in imports) import.ReleasePins();
        foreach (var replacement in platformReplacements) replacement.ReleasePins();
    }

    private sealed class RemovalOperation(RemovedProjects removal) : Operation
    {
        protected override void Rollback() => removal.Change(false);
        protected override void Rollforward() => removal.Change(true);
    }

    private sealed class RemovedProjects
    {
        private readonly WorkspaceMembershipOwner owner;
        private readonly PackageContainer[] before, selected, after;
        private readonly Stride.Core.Solutions.Project[] nativeBefore, nativeAfter;
        private readonly Guid? startupBefore, startupAfter;
        private readonly EditorAuthoringState[] states;
        private readonly Dictionary<EditorAuthoringState, IDisposable> leases = [];
        private readonly Action refreshed;
        private readonly PlatformReplacementAuthority? replacements;
        private readonly Action<EditorAuthoringState[], bool> synchronizeViews;
        private bool? recoveryGoal;
        private readonly long beforeRevision, afterRevision;
        private readonly Dictionary<PackageContainer, (Dependency[] Before, Dependency[] After)> resolved;
        private bool applied;
        internal EditorAuthoringState[] States => states;
        private PackageSession Live => owner.Workspace.Session;
        private EditorAuthoringRegistry Registry => owner.SessionHistory!.Registry;

        internal RemovedProjects(WorkspaceMembershipOwner owner, PackageContainer[] selected, Action refreshed, Action<EditorAuthoringState[], bool> synchronizeViews, PlatformReplacementAuthority? replacements)
        {
            this.owner = owner; this.selected = selected.ToArray(); this.refreshed = refreshed; this.synchronizeViews = synchronizeViews; this.replacements = replacements;
            before = Live.Projects.ToArray(); after = before.Except(selected).ToArray();
            nativeBefore = Live.AuthoringSolution.Projects.ToArray();
            var removedIds = selected.OfType<SolutionProject>().Select(project => project.Id).ToHashSet();
            nativeAfter = nativeBefore.Where(project => !removedIds.Contains(project.Guid)).ToArray();
            startupBefore = Live.AuthoringSolution.StartupProjectGuid;
            startupAfter = startupBefore is { } startup && removedIds.Contains(startup) ? null : startupBefore;
            resolved = before.Except(selected).ToDictionary(container => container, container =>
                (container.FlattenedDependencies.ToArray(), container.FlattenedDependencies.Where(dependency => dependency.Package == null
                    || !selected.Any(target => ReferenceEquals(target.Package, dependency.Package))).ToArray()));
            ValidateSelected();
            var ids = selected.SelectMany(project => project.Package.Assets).Select(item => item.Id).ToHashSet();
            var paths = selected.SelectMany(project => project is SolutionProject solutionProject
                ? new[] { project.Package.FullPath.ToOSPath(), project.Package.FullPath.ToOSPath() + ".user", solutionProject.FullPath.ToOSPath() }
                : new[] { project.Package.FullPath.ToOSPath(), project.Package.FullPath.ToOSPath() + ".user" })
                .Select(System.IO.Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            states = Registry.PublishedStates.Where(state => !ReferenceEquals(state.Document, owner.Document) && (state.AssetId is { } id && ids.Contains(id) || paths.Contains(state.CanonicalPath))).ToArray();
            try { foreach (var state in states) leases.Add(state, state.AcquireHistory()); }
            catch { ReleasePins(); throw; }
            beforeRevision = owner.Revision; afterRevision = owner.AllocateRevision();
        }

        private void ValidateSelected()
        {
            if (selected.Length == 0 || selected.Distinct().Count() != selected.Length
                || selected.Any(project => !before.Contains(project) || project.Package.IsReadOnly)
                || selected.Any(project => ReferenceEquals(project, Live.CurrentProject) || ReferenceEquals(project.Package, owner.Workspace.Package)))
                throw new InvalidOperationException("Switch to a remaining project before removing its active package.");
            if (before.Except(selected).Any(project => project.DirectDependencies.Any(dependency =>
                selected.Any(target => PackageDependencies.RefersTo(target.Package, dependency)
                    && !(replacements?.Allows(target, project, dependency) ?? false)))))
                throw new InvalidOperationException("Remove the remaining projects' dependencies before removing this package.");
        }

        internal void Change(bool removing)
        {
            if (recoveryGoal is { } recovery)
            {
                if (owner.Revision != beforeRevision && owner.Revision != afterRevision
                    || Live.Projects.Any(container => !before.Contains(container))
                    || !Live.Projects.Except(selected).SequenceEqual(before.Except(selected))
                    || Live.AuthoringSolution.Projects.Any(project => !nativeBefore.Contains(project))
                    || !Live.AuthoringSolution.Projects.Where(project => nativeAfter.Contains(project)).SequenceEqual(nativeAfter))
                    throw new InvalidOperationException("The failed removal journal conflicts with newer session membership.");
                foreach (var state in states)
                    if (state.IsDisposed || Registry.Find(state.CanonicalPath) is { } occupied && !ReferenceEquals(occupied, state))
                        throw new InvalidOperationException("The failed removal journal conflicts with a newer authored owner.");
                Apply(recovery); recoveryGoal = null;
            }
            if (applied == removing) return;
            var expected = removing ? before : after;
            var nativeExpected = removing ? nativeBefore : nativeAfter;
            if (owner.Revision != (removing ? beforeRevision : afterRevision)
                || !Live.Projects.SequenceEqual(expected) || !Live.AuthoringSolution.Projects.SequenceEqual(nativeExpected)
                || Live.AuthoringSolution.StartupProjectGuid != (removing ? startupBefore : startupAfter))
                throw new InvalidOperationException("The session membership changed after package removal preparation.");
            if (removing) ValidateSelected();
            foreach (var pair in resolved)
                if (!pair.Key.FlattenedDependencies.SequenceEqual(removing ? pair.Value.Before : pair.Value.After))
                    throw new InvalidOperationException("The native resolved dependency ownership changed.");
            foreach (var state in states)
                if (state.IsDisposed || Registry.Find(state.CanonicalPath) is { } occupied && !ReferenceEquals(occupied, state))
                    throw new InvalidOperationException("A removed package document has another canonical owner.");
            try { Apply(removing); }
            catch (Exception failure)
            {
                try { Apply(!removing); }
                catch (Exception restoration) { recoveryGoal = !removing; throw new AggregateException("Package removal and restoration failed.", failure, restoration); }
                throw;
            }
        }

        private void Apply(bool removing)
        {
            if (removing) synchronizeViews(states, true);
            foreach (var pair in resolved)
            {
                var target = removing ? pair.Value.After : pair.Value.Before;
                if (!pair.Key.FlattenedDependencies.SequenceEqual(target))
                { pair.Key.FlattenedDependencies.Clear(); foreach (var dependency in target) pair.Key.FlattenedDependencies.Add(dependency); }
            }
            foreach (var container in selected)
            {
                if (removing)
                {
                    if (Live.Projects.Contains(container)) Live.Projects.Remove(container);
                }
                else if (!Live.Projects.Contains(container))
                {
                    if (container.Session != null) throw new InvalidOperationException("The detached package has another session owner.");
                    Live.Projects.Insert(Math.Min(Array.IndexOf(before, container), Live.Projects.Count), container);
                }
            }
            // Native removal also owns the actual VSProject collection. Restore its
            // captured original order without reserializing or replacing project models.
            var native = removing ? nativeAfter : nativeBefore;
            for (var index = 0; index < native.Length; index++)
            {
                var project = native[index];
                var position = Live.AuthoringSolution.Projects.IndexOf(project);
                if (position == index) continue;
                if (position >= 0) Live.AuthoringSolution.Projects.Remove(project);
                Live.AuthoringSolution.Projects.Insert(index, project);
            }
            Live.AuthoringSolution.StartupProjectGuid = removing ? startupAfter : startupBefore;
            foreach (var state in states)
                if (removing) { if (state.IsPublished) Registry.Unpublish(state); }
                else Registry.Publish(state);
            if (!removing) synchronizeViews(states, false);
            owner.Revision = removing ? afterRevision : beforeRevision;
            Live.IsDirty = true; applied = removing;
            owner.Workspace.RefreshAssets(); refreshed();
        }

        private void ReleasePins()
        { foreach (var pair in leases.ToArray()) { pair.Value.Dispose(); leases.Remove(pair.Key); } }
        internal void ReleaseForSessionTeardown()
        {
            // The original session still owns shared assembly/resolver registrations.
            // Reattach detached native containers only for that session's final Dispose;
            // this phase never saves solution membership or resurrects editor views.
            foreach (var container in selected)
                if (container.Session == null) Live.Projects.Add(container);
                else if (!ReferenceEquals(container.Session, Live)) throw new InvalidOperationException("The removed package has another session owner.");
            ReleasePins();
        }
    }
}
