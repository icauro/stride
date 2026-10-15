using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawContentReferenceFetchActions(IEnumerable<object?> values)
    {
        var ids = values.Select(ContentAssetReference.Read).Where(reference => reference != null)
            .Select(reference => reference!.Id).Distinct().ToArray();
        var available = !IsAuthoringPipelineUpdating && workspace != null && ids.Any(id => workspace.AssetItems.Any(item => item.Id == id && !item.IsDeleted));
        if (DrawNativeMenuItem(EditorLocalization.Text("Select the referenced asset"), "", false, available))
            FetchContentReferences(values);
    }
    private void FetchContentReferences(IEnumerable<object?> values)
    {
        var ids = values.Select(ContentAssetReference.Read).Where(reference => reference != null)
            .Select(reference => reference!.Id).Distinct().ToArray();
        Execute(() =>
            {
                if (workspace == null || IsAuthoringPipelineUpdating) return;
                foreach (var id in ids)
                    if (assetBrowser.RevealAsset(workspace, id) is { } entry)
                    { selectedAssetPath = entry.Path; ActivateAssetPropertyContext(); }
            });
    }
}
