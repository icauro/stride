using Stride.Assets.Presentation.SceneEditor;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly HighlightRenderState materialHighlight = new();
    internal bool MaterialSelectionEnabled { get; set; }
    internal Entity? HighlightedMaterialEntity { get; private set; }
    internal int HighlightedMaterialIndex { get; private set; } = -1;
    internal int HighlightedMaterialMesh { get; private set; } = -1;
    private int highlightedMaterialComponent;

    private void InitializeMaterialHighlight()
    {
        var stage = new RenderStage("Highlight", "Highlight") { Filter = new MaterialHighlightFilter(materialHighlight) };
        loadedCompositor!.RenderStages.Add(stage);
        foreach (var feature in loadedCompositor.RenderFeatures.OfType<MeshRenderFeature>())
        {
            feature.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector
            { EffectName = "StrideEditorForwardShadingEffect.Highlight", RenderStage = stage, RenderGroup = RenderGroupMask.All });
            feature.RenderFeatures.Add(new HighlightRenderFeature { State = materialHighlight });
        }
        ((EditorTopLevelCompositor)loadedCompositor.Game).PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = stage, Name = "Material Highlighting" });
    }

    internal void ClearMaterialHighlight()
    {
        materialHighlight.ModelHighlightColors.Clear(); materialHighlight.MaterialsHighlightedForModel.Clear(); HighlightedMaterialEntity = null;
        HighlightedMaterialIndex = HighlightedMaterialMesh = -1;
        highlightedMaterialComponent = 0;
    }

    internal bool HighlightMaterial(Entity? entity, int componentId, int materialIndex, int meshIndex)
    {
        ClearMaterialHighlight();
        if (!MaterialSelectionEnabled || entity == null || !CanPickSceneEntity(entity)
            || RuntimeOwner(entity) is not { } owner || !owner.IsSelected(entity.Id)
            || !ReferenceEquals(ResolveRuntimeEntity(owner, entity.Id), entity)) return false;
        var model = entity.Get<ModelComponent>();
        if (model?.Model == null || RuntimeIdHelper.ToRuntimeId(model) != componentId
            || materialIndex < 0 || meshIndex < 0 || meshIndex >= model.Model.Meshes.Count) return false;
        var material = model.GetMaterial(materialIndex);
        if (material == null) return false;
        materialHighlight.MaterialsHighlightedForModel.Add(material);
        var color = Color4.PremultiplyAlpha(new Color4(1f, .35f, .25f, .8f));
        materialHighlight.ModelHighlightColors[model] = color;
        foreach (var selectedOwner in PresentedHierarchyDocuments)
            foreach (var id in selectedOwner.SelectedIds)
                if (ResolveRuntimeEntity(selectedOwner, id)?.Get<ModelComponent>() is { Model: not null } selected)
                    materialHighlight.ModelHighlightColors[selected] = color;
        HighlightedMaterialEntity = entity; HighlightedMaterialIndex = materialIndex; HighlightedMaterialMesh = meshIndex;
        highlightedMaterialComponent = componentId;
        return true;
    }

    private void RefreshMaterialHighlight()
    {
        if (HighlightedMaterialEntity is { } entity)
            HighlightMaterial(entity, highlightedMaterialComponent, HighlightedMaterialIndex, HighlightedMaterialMesh);
    }

    private bool TryFocusHighlightedMaterial()
    {
        var entity = HighlightedMaterialEntity;
        if (!MaterialSelectionEnabled || entity == null || RuntimeOwner(entity) is not { } owner
            || !owner.IsSelected(entity.Id) || !ReferenceEquals(ResolveRuntimeEntity(owner, entity.Id), entity)
            || !owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var authored)) return false;
        Focus(owner, authored.Entity, HighlightedMaterialMesh);
        return true;
    }

    private sealed class MaterialHighlightFilter(HighlightRenderState state) : RenderStageFilter
    {
        public override bool IsVisible(RenderObject renderObject, RenderView renderView, RenderViewStage renderViewStage)
            => renderObject is RenderMesh mesh && (state.MaterialHighlightColors.ContainsKey(mesh.MaterialPass.Material)
                || state.MeshHighlightColors.ContainsKey(mesh.Mesh)
                || state.MaterialsHighlightedForModel.Contains(mesh.MaterialPass.Material)
                    && mesh.Source is ModelComponent component && state.ModelHighlightColors.ContainsKey(component));
    }
}
