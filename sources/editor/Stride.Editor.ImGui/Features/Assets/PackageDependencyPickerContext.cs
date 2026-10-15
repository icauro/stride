using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record PackageDependencyPickerContext(ProjectWorkspace Workspace, EditorSessionHistory History,
    SolutionProject Project, ProjectBuildSettingsDocument Owner, long Revision)
{
    internal void Validate(ProjectWorkspace? workspace, EditorSessionHistory? history)
    {
        if (!ReferenceEquals(workspace, Workspace) || !ReferenceEquals(history, History)
            || !ReferenceEquals(Project.Package.Session, Workspace.Session) || !Workspace.Session.LocalPackages.Contains(Project.Package)
            || Project.Package.IsReadOnly || Project.Package.State < PackageState.AssetsReady
            || !ReferenceEquals(History.Registry.Find(Owner.Path)?.Document, Owner) || Owner.AuthoringRevision != Revision)
            throw new InvalidOperationException("The package changed while selecting its dependency.");
        Owner.ValidateProjectFileBaseline();
    }
}
