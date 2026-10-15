using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageMetadataDocument
{
    internal void RemoveSessionDependencies(DependencyRange[] removing, Action validate)
    {
        historyScope.EnsureMaySave();
        if (package.Container is not StandalonePackage container || package.IsReadOnly
            || !ReferenceEquals(container.Session, workspace.Session)) throw new InvalidOperationException("This package can't be edited.");
        validate();
        var operation = new SessionDependencyRemoval(this, container, container.DirectDependencies.Except(removing).ToArray(), forward => { if (forward) validate(); });
        historyScope.Record(operation);
        operation.Change(true);
    }
    internal void AddSessionDependency(DependencyRange dependency, Action<bool> validate)
    {
        historyScope.EnsureMaySave();
        if (package.Container is not StandalonePackage container || package.IsReadOnly
            || !ReferenceEquals(container.Session, workspace.Session)) throw new InvalidOperationException("This package can't be edited.");
        validate(true);
        var operation = new SessionDependencyRemoval(this, container, [.. container.DirectDependencies, dependency], validate);
        historyScope.Record(operation); operation.Change(true);
    }
    private sealed class SessionDependencyRemoval : Operation
    {
        private readonly PackageMetadataDocument owner;
        private readonly StandalonePackage container;
        private readonly DependencyRange[] before, after;
        private readonly (string Name, string Version, DependencyType Type, string Project)[] declarations;
        private readonly Action<bool> validate;
        private readonly long beforeRevision, afterRevision;
        private DependencyRange[]? failedReceipt, recoveryTarget;
        private long recoveryRevision, recoveryGoalRevision;
        private bool applied;
        internal SessionDependencyRemoval(PackageMetadataDocument owner, StandalonePackage container, DependencyRange[] next, Action<bool> validate)
        {
            this.owner = owner; this.container = container; this.validate = validate;
            before = container.DirectDependencies.ToArray(); after = next;
            declarations = Capture(before.Concat(after).Distinct()); beforeRevision = owner.Revision; afterRevision = owner.AllocateRevision();
        }
        private static (string, string, DependencyType, string)[] Capture(IEnumerable<DependencyRange> source)
            => source.Select(value => (value.Name, value.VersionRange?.ToString() ?? "", value.Type, value.MSBuildProject)).ToArray();
        internal void Change(bool forward)
        {
            owner.historyScope.EnsureMaySave();
            if (!ReferenceEquals(container.Session, owner.workspace.Session) || container.Package.IsReadOnly
                || !Capture(before.Concat(after).Distinct()).SequenceEqual(declarations)) throw new InvalidOperationException("The package dependencies changed.");
            if (failedReceipt != null)
            {
                if (!container.DirectDependencies.SequenceEqual(failedReceipt) || owner.Revision != recoveryRevision)
                    throw new InvalidOperationException("The failed dependency edit conflicts with newer package dependencies.");
                try { Install(recoveryTarget!, recoveryGoalRevision); failedReceipt = recoveryTarget = null; }
                catch { failedReceipt = container.DirectDependencies.ToArray(); recoveryRevision = owner.Revision; throw; }
            }
            if (applied == forward) return;
            var expected = forward ? before : after;
            var expectedRevision = forward ? beforeRevision : afterRevision;
            if (owner.Revision != expectedRevision || !container.DirectDependencies.SequenceEqual(expected))
                throw new InvalidOperationException("The package dependencies changed.");
            validate(forward);
            try { Install(forward ? after : before, forward ? afterRevision : beforeRevision); applied = forward; }
            catch (Exception failure)
            {
                try { Install(expected, expectedRevision); }
                catch (Exception compensation)
                {
                    failedReceipt = container.DirectDependencies.ToArray(); recoveryTarget = expected; recoveryRevision = owner.Revision; recoveryGoalRevision = expectedRevision;
                    throw new AggregateException("Dependency removal and restoration failed.", failure, compensation);
                }
                throw;
            }
        }
        private void Install(DependencyRange[] target, long revision)
        {
            container.DirectDependencies.Clear(); foreach (var item in target) container.DirectDependencies.Add(item);
            owner.RestoreRevision(revision);
        }
        protected override void Rollback() => Change(false);
        protected override void Rollforward() => Change(true);
    }
}
