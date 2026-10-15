namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    // WPF keeps the real mesh pass available when the persistent mask is hidden,
    // allowing the shared wireframe feature's one-second selection-change pulse.
    private void ConfigureSelectionMaskRendering()
    {
        foreach (var feature in selectionFeatures)
        {
            feature.IsEnabled = () => selectedModels.Count != 0 || ComponentGizmos?.HasSelectedLightProbes == true;
            feature.DisplaySelectionMask = () => ShowSelection;
        }
        if (SelectionRenderer != null) SelectionRenderer.IsEnabled = () => selectedModels.Count != 0;
    }
}
