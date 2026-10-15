using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    public void ChangeCompilerReference(ProjectCompilerReferenceChange change, string configuration, string framework)
    {
        ValidateProjectFileBaseline();
        var before = properties.Snapshot; var beforeRevision = revision;
        try
        {
            properties.ChangeCompilerReference(change, configuration, framework);
            var after = properties.Snapshot;
            if (before == after) return;
            revision = ++nextRevision;
            using var transaction = history.CreateTransaction(change.Included ? "Add compiler reference" : "Remove compiler reference");
            history.Record(new BuildOperation(this, before, after, beforeRevision, revision));
        }
        catch { Apply(before, beforeRevision); throw; }
    }
}
