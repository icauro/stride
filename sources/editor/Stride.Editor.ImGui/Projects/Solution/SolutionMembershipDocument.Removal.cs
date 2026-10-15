namespace Stride.Editor.ImGui;

internal sealed partial class SolutionMembershipDocument
{
    private WorkspaceMembershipOwner? removalMembership;
    internal WorkspaceMembershipOwner GetRemovalMembership()
        => removalMembership ??= new(workspace, this, history, () => revision, () => ++nextRevision, value => revision = value);
    private void ReleaseRemovedProjects() => removalMembership?.ReleaseRetainedStates();
}
