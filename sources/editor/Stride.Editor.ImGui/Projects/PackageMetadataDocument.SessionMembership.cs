namespace Stride.Editor.ImGui;

internal sealed partial class PackageMetadataDocument
{
    private WorkspaceMembershipOwner? sessionMembership;
    internal WorkspaceMembershipOwner GetSessionMembership()
        => sessionMembership ??= new(workspace, this, historyScope, () => revision, AllocateRevision, RestoreRevision);
}
