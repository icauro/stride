using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool sceneMaterialSelection;
    private void SaveSceneMaterialSelectionState()
    {
        if (IsRecoveryWorkspacePresentationRestoring) return;
        if (CurrentSceneEditorTransientState() is { } state) state.MaterialSelection = sceneMaterialSelection;
    }
    private void RestoreSceneMaterialSelectionState()
    {
        sceneMaterialSelection = CurrentSceneEditorTransientState()?.MaterialSelection ?? false;
        if (engineViewport == null) return;
        engineViewport.Picker?.Cancel("Material selection editor changed");
        engineViewport.ClearMaterialHighlight();
        engineViewport.MaterialSelectionEnabled = sceneMaterialSelection;
    }
    internal string? HighlightedSceneMaterialName
    {
        get
        {
            if (engineViewport is not { MaterialSelectionEnabled: true, HighlightedMaterialEntity: { } entity } viewport
                || viewport.RuntimeOwner(entity) is not { } owner || !owner.IsSelected(entity.Id)
                || !owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || part.Entity.Get<ModelComponent>() is not { } component)
                return null;
            var layout = ModelMaterialLayout(component);
            var index = viewport.HighlightedMaterialIndex;
            return layout != null && index >= 0 && index < layout.Materials.Count ? layout.Materials[index].Name : null;
        }
    }
    private bool IsSceneMaterialSlotHighlighted(string name)
        => !string.IsNullOrEmpty(name) && string.Equals(HighlightedSceneMaterialName, name, StringComparison.Ordinal);
    private void DrawSceneMaterialSelectionToolbar(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        var selectedMaterialMode = context == null ? sceneMaterialSelection : engineViewport!.MaterialSelectionEnabled;
        if (SceneToolbarIcon("scene-toolbar-material-selection", "Toggle material selection (click a selected asset to select its material)", selectedMaterialMode))
        {
            selectedMaterialMode = !selectedMaterialMode;
            if (context == null) { sceneMaterialSelection = selectedMaterialMode; SaveSceneMaterialSelectionState(); }
            engineViewport!.Picker?.Cancel("Material selection mode changed"); if (context == null) pickDocument = null;
            engineViewport.ClearMaterialHighlight();
        }
        engineViewport!.MaterialSelectionEnabled = selectedMaterialMode;
        RegisterVisualCaptureItem("scene.material-selection");
    }

    private bool CompleteSceneMaterialPick(EditorPickingRenderer picker)
    {
        if (picker.Purpose == EditorPickingPurpose.Selection) return false;
        picker.Consume();
        if (engineViewport == null || !sceneMaterialSelection || picker.Error != null)
        { if (picker.Error != null) statusMessage = picker.Error; engineViewport?.ClearMaterialHighlight(); return true; }
        var highlighted = engineViewport.HighlightMaterial(picker.Result, picker.ComponentId, picker.MaterialIndex, picker.MeshNodeIndex);
        if (!highlighted && picker.Purpose == EditorPickingPurpose.MaterialClick) return false;
        if (highlighted && picker.Purpose == EditorPickingPurpose.MaterialClick) RevealPickedSceneMaterial(picker.Result, picker.MaterialIndex);
        if (picker.Purpose == EditorPickingPurpose.MaterialClick) pickDocument = null;
        return true;
    }

    private void RevealPickedSceneMaterial(Entity runtime, int materialIndex)
    {
        var owner = engineViewport?.RuntimeOwner(runtime);
        if (workspace == null || owner == null || !owner.Asset.Hierarchy.Parts.TryGetValue(runtime.Id, out var part)
            || !owner.IsSelected(runtime.Id)) return;
        var component = part.Entity.Get<ModelComponent>();
        if (component == null) return;
        component.Materials.TryGetValue(materialIndex, out Material? material);
        if (material == null && ModelMaterialLayout(component) is { } model && materialIndex >= 0 && materialIndex < model.Materials.Count)
            material = model.Materials[materialIndex].MaterialInstance.Material;
        if (AttachedReferenceManager.GetAttachedReference(material) is not { } reference) return;
        if (assetBrowser.RevealAsset(workspace, reference.Id) is not { } entry) return;
        selectedAssetPath = entry.Path; showAssets = true;
    }

    private void RequestSceneMaterialHover(System.Numerics.Vector2 size, bool hovered)
    {
        if (!sceneMaterialSelection || engineViewport?.Picker is not { } picker) return;
        if (!hovered || cameraGesture || engineViewport.Gizmos?.Captured == true)
        {
            engineViewport.ClearMaterialHighlight();
            if (picker.Purpose != EditorPickingPurpose.Selection) picker.Cancel("Material hover ended");
            return;
        }
        if (picker.IsPending || picker.HasResult) return;
        var normalized = (Gui.GetIO().MousePos - SceneImageMinimum) / size;
        if (normalized.X < 0 || normalized.X >= 1 || normalized.Y < 0 || normalized.Y >= 1) return;
        if (picker.DisplayedSize.X <= 0 || picker.DisplayedSize.Y <= 0) return;
        picker.Request(new Vector2(normalized.X, normalized.Y), picker.DisplayedViewProjection, picker.DisplayedSize,
            purpose: EditorPickingPurpose.MaterialHover);
    }
}
