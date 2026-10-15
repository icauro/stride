namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyProjectParityDependencies(string ownedDirectory)
    {
        if (workspace == null || SessionAuthoringHistory == null)
            throw new InvalidOperationException("Project verification requires a loaded native workspace.");
        if (projectLoading != null || IsAuthoringPipelineUpdating || IsUserProjectBuilding)
            throw new InvalidOperationException("Project verification requires an idle editor actor.");
        var workspaceOwner = workspace;
        var historyOwner = SessionAuthoringHistory;
        var members = workspace.Session.Projects.ToArray();
        var result = ProjectParityDependencyVerification.Verify(ownedDirectory);
        if (!ReferenceEquals(workspace, workspaceOwner) || !ReferenceEquals(SessionAuthoringHistory, historyOwner)
            || !workspace.Session.Projects.SequenceEqual(members))
            throw new InvalidOperationException("Private dependency verification changed the loaded editor workspace.");
        return result;
    }
}
