using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    public void ChangeDependency(DependencyRange? before, DependencyRange? after, Action<IReadOnlyList<DependencyRange>> validateScope)
    {
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        var previous = CloneDependencies(project.DirectDependencies);
        if (before != null && !project.DirectDependencies.Contains(before)) throw new InvalidOperationException("The dependency changed.");
        var next = project.DirectDependencies.Select(dependency => ReferenceEquals(dependency, before) ? after : dependency).Where(dependency => dependency != null).Cast<DependencyRange>().ToList();
        if (before == null && after != null) next.Add(after);
        var updated = CloneDependencies(next);
        validateScope(updated);
        var beforeXml = properties.Snapshot; var beforeRevision = revision; var afterRevision = ++nextRevision;
        try
        {
            properties.SetDependencyReference(before, after, Path);
            var afterXml = properties.Snapshot;
            ApplyDependencyState(afterXml, updated, afterRevision);
            afterXml = properties.Snapshot;
            using var transaction = history.CreateTransaction(before == null ? "Add dependency to package '" + after!.Name + "'"
                : after == null ? "Delete dependency '" + before.Name + "'" : "Update dependency '" + before.Name + "'");
            history.Record(new DependencyOperation(this, beforeXml, afterXml, previous, updated, beforeRevision, afterRevision, validateScope));
        }
        catch { ApplyDependencyState(beforeXml, previous, beforeRevision); throw; }
    }
    internal void RemoveDependencies(IReadOnlyCollection<DependencyRange> removing, Action<IReadOnlyList<DependencyRange>> validateScope)
    {
        if (!CanSave || removing.Count == 0 || removing.Any(dependency => !project.DirectDependencies.Contains(dependency)))
            throw new InvalidOperationException("The dependencies changed or their project is read-only.");
        var previous = CloneDependencies(project.DirectDependencies);
        var next = CloneDependencies(project.DirectDependencies.Except(removing));
        validateScope(next);
        var beforeXml = properties.Snapshot; var beforeRevision = revision; var afterRevision = ++nextRevision;
        try
        {
            foreach (var dependency in removing) properties.SetDependencyReference(dependency, null, Path);
            var afterXml = properties.Snapshot;
            ApplyDependencyState(afterXml, next, afterRevision);
            afterXml = properties.Snapshot;
            using var transaction = history.CreateTransaction("Delete dependencies");
            history.Record(new DependencyOperation(this, beforeXml, afterXml, previous, next, beforeRevision, afterRevision, validateScope));
        }
        catch { ApplyDependencyState(beforeXml, previous, beforeRevision); throw; }
    }

    private void ApplyDependencyState(string xml, DependencyRange[] dependencies, long nextRevisionValue)
    {
        var previousXml = properties.Snapshot; var previous = CloneDependencies(project.DirectDependencies); var previousRevision = revision;
        try { properties.Restore(xml); project.ApplyAuthoredDirectDependencies(CloneDependencies(dependencies)); revision = nextRevisionValue; SynchronizeProject(); }
        catch (Exception failure)
        {
            try { properties.Restore(previousXml); project.ApplyAuthoredDirectDependencies(previous); revision = previousRevision; SynchronizeProject(); }
            catch (Exception recovery) { throw new AggregateException("The dependency edit could not be restored.", failure, recovery); }
            throw;
        }
    }
    private static DependencyRange[] CloneDependencies(IEnumerable<DependencyRange> dependencies) => dependencies.Select(dependency =>
        new DependencyRange(dependency.Name, dependency.VersionRange == null ? null! : new PackageVersionRange(dependency.VersionRange.MinVersion,
            dependency.VersionRange.IsMinInclusive, dependency.VersionRange.MaxVersion, dependency.VersionRange.IsMaxInclusive), dependency.Type)
        { MSBuildProject = dependency.MSBuildProject }).ToArray();
    private static bool DependenciesMatch(IEnumerable<DependencyRange> actual, IEnumerable<DependencyRange> expected) =>
        actual.Select(dependency => (dependency.Name, dependency.Type, dependency.VersionRange?.ToString(), dependency.MSBuildProject))
            .SequenceEqual(expected.Select(dependency => (dependency.Name, dependency.Type, dependency.VersionRange?.ToString(), dependency.MSBuildProject)));
    private sealed class DependencyOperation(ProjectBuildSettingsDocument owner, string beforeXml, string afterXml, DependencyRange[] before,
        DependencyRange[] after, long beforeRevision, long afterRevision, Action<IReadOnlyList<DependencyRange>> validateScope) : Operation
    {
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            if (owner.properties.Snapshot != (forward ? beforeXml : afterXml)
                || !DependenciesMatch(owner.project.DirectDependencies, forward ? before : after))
            {
                static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
                var expectedXml = forward ? beforeXml : afterXml;
                var expectedDependencies = forward ? before : after;
                static string Describe(IEnumerable<DependencyRange> dependencies) => string.Join(";", dependencies.Select(dependency =>
                    $"{dependency.Name}|{dependency.Type}|{dependency.VersionRange}|{dependency.MSBuildProject}"));
                throw new InvalidOperationException($"The package dependencies changed. forward={forward}, xmlMatch={owner.properties.Snapshot == expectedXml}, actualXmlHash={Hash(owner.properties.Snapshot)}, expectedXmlHash={Hash(expectedXml)}, dependenciesMatch={DependenciesMatch(owner.project.DirectDependencies, expectedDependencies)}, actualDependenciesHash={Hash(Describe(owner.project.DirectDependencies))}, expectedDependenciesHash={Hash(Describe(expectedDependencies))}, actualRevision={owner.revision}, expectedRevision={(forward ? beforeRevision : afterRevision)}.");
            }
            var next = forward ? after : before;
            validateScope(next);
            var expectedRevision = forward ? beforeRevision : afterRevision;
            try
            {
                owner.ApplyDependencyState(forward ? afterXml : beforeXml, next, forward ? afterRevision : beforeRevision);
            }
            catch
            {
                owner.ApplyDependencyState(forward ? beforeXml : afterXml, forward ? before : after, expectedRevision);
                throw;
            }
        }
    }
}
