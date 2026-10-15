using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly HashSet<WorkspaceMembershipOwner> packageSessionMembershipOwners = [];
    private WorkspaceMembershipOwner GetWorkspaceRemovalMembership(PackageContainer[] removing)
    {
        if (workspace is not { } current || SessionAuthoringHistory is not { } history)
            throw new InvalidOperationException("The authored package session is no longer current.");
        if (!string.IsNullOrEmpty(current.Session.SolutionPath?.ToOSPath())) return GetSolutionMembershipOwner().GetRemovalMembership();
        // The real package-file owner survives as session authority when the last
        // native container is detached; its on-disk file remains unchanged.
        // This is not a fake solution path
        // or a second document registered against the same file.
        var package = current.Session.LocalPackages.FirstOrDefault(package => ReferenceEquals(package, current.Package)
            && !removing.Any(container => ReferenceEquals(container.Package, package)))
            ?? current.Session.LocalPackages.FirstOrDefault(package => !removing.Any(container => ReferenceEquals(container.Package, package))
                && !package.IsReadOnly && package.State >= PackageState.AssetsReady)
            ?? removing.Select(container => container.Package).FirstOrDefault(package => !package.IsReadOnly
                && package.State >= PackageState.AssetsReady)
            ?? throw new InvalidOperationException("An actual editable package must own the session membership.");
        var state = history.Registry.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(current, package, history));
        var owner = state.Document.GetSessionMembership(); packageSessionMembershipOwners.Add(owner); return owner;
    }
    private void ReleasePackageSessionMembershipOwnership()
    {
        ClearEmptyWorkspaceRuntimeState();
        foreach (var owner in packageSessionMembershipOwners.ToArray())
        { owner.ReleaseRetainedStates(); packageSessionMembershipOwners.Remove(owner); }
    }
}
