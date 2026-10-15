using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<ProjectBuildSettingsDocument, (long Revision, AuthoredProjectSnapshot Snapshot)> scriptProjectSnapshots = [];
    private string? scriptProjectSnapshotError;
    private AuthoredProjectSnapshot[] CaptureScriptProjectSnapshots()
    {
        var owners = authoring?.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
            .Where(document => document.IsDirty).OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        foreach (var old in scriptProjectSnapshots.Keys.Except(owners).ToArray()) scriptProjectSnapshots.Remove(old);
        var result = new List<AuthoredProjectSnapshot>();
        foreach (var owner in owners)
        {
            if (!scriptProjectSnapshots.TryGetValue(owner, out var retained) || retained.Revision != owner.AuthoringRevision)
            {
                retained = (owner.AuthoringRevision, owner.CaptureProjectSnapshot());
                scriptProjectSnapshots[owner] = retained;
            }
            result.Add(retained.Snapshot);
        }
        return result.ToArray();
    }
}
