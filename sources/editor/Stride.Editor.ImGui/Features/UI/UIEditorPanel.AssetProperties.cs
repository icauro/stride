namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawUIPropertiesCommand(UIDocument source)
    {
        if (host.DrawUIPropertiesButton())
            QueueUICommand(source, selected.ToArray(), false, (asset, _) =>
            {
                // Native ShowAssetProperties changes the inspector selection, not the editor's selected parts.
                showUIAssetProperties = true;
                asset.SelectExtensionElements([]);
                host.SelectionChanged();
            });
        host.RegisterCaptureItem("ui.properties", "control", "UI properties");
    }
}
