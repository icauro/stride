namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ClearSharedInspectorCaches()
    {
        aggregatePropertyOwners.Clear(); aggregateOwnerVersions.Clear(); aggregatePropertyDrafts.Clear();
        aggregateProperties = []; presentedAggregateProperties = null; aggregatePanels = [];
        commonInspectorDocument = null; commonInspectorSelection = commonInspectorStructure = -1;
        commonPropertyPanels = []; commonPropertyDrafts.Clear();
        componentErrors.Clear(); PropertyGrid.ModelNodeNames.Clear();
        pendingObjectProperty = null; pendingCollectionAction = null;
        cameraSlotAssets = null; cameraSlotPackage = null; cameraSlotChoices = [];
    }
}
