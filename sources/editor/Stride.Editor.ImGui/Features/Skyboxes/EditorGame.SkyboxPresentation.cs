namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CloseSkyboxPreview()
    {
        if (skyboxPreview != null)
        {
            skyboxPreview.Visible = false;
            retiredSkyboxResources.Add((skyboxPreview, frames + 2));
            skyboxPreview = null;
        }
        requestedSkyboxDocument = null;
        pendingSkyboxRevision = null;
    }

    private void RebindSkyboxSettingsDocument(IEditorDocument previous, IEditorDocument next)
    {
        if (ReferenceEquals(requestedSkyboxDocument, previous))
        {
            requestedSkyboxDocument = next is AssetMetadataDocument { SkyboxSettings: not null } skybox ? skybox : null;
            pendingSkyboxRevision = null;
        }
        if (ReferenceEquals(compilingSkyboxDocument, previous)) skyboxCancellation?.Cancel();
    }
}
