using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Engine;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class MaterialLayout(List<ModelMaterial> materials) : IModelAsset { public List<ModelMaterial> Materials { get; } = materials; }
    private sealed record MaterialLayoutCache(object Asset, long Revision, MaterialLayout Layout);
    private IReadOnlyList<AssetItem>? modelMaterialAssets;
    private readonly Dictionary<AssetId, MaterialLayoutCache> materialLayouts = [];

    private IModelAsset? ModelMaterialLayout(ModelComponent component)
    {
        if (!ReferenceEquals(modelMaterialAssets, workspace?.AssetItems))
        { modelMaterialAssets = workspace?.AssetItems; materialLayouts.Clear(); }
        var reference = ContentAssetReference.Read(component.Model);
        var item = reference == null ? null : workspace?.AssetItems.FirstOrDefault(asset => asset.Id == reference.Id);
        if (item == null) return null;
        var retained = documents.Find(item.FullPath.ToOSPath())?.Document as IAssetTagDocument;
        var asset = retained?.AuthoredAsset ?? item.Asset;
        if (asset is not IModelAsset model) return null;
        var revision = retained?.EditRevision ?? 0;
        if (materialLayouts.TryGetValue(item.Id, out var cached) && ReferenceEquals(cached.Asset, asset) && cached.Revision == revision) return cached.Layout;
        var layout = new MaterialLayout(model.Materials.ToList());
        materialLayouts[item.Id] = new(asset, revision, layout); return layout;
    }

    private void DrawModelMaterialSlots(ComponentField field)
    {
        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
        Gui.PushID(field.Name); Gui.SeparatorText(field.Label);
        var owner = document!; var model = ModelMaterialLayout((ModelComponent)field.Component);
        if (model != null)
            for (var index = 0; index < model.Materials.Count; index++)
            {
                var slot = index; var descriptor = model.Materials[index];
                Gui.PushID(index);
                if (!BeginInspectorPropertyRow("", drawLabel: false)) { Gui.PopID(); continue; }
                var enabled = owner.IsModelMaterialEnabled(field, index);
                var canEdit = owner.CanEditModelMaterialSlot(field, slot);
                Gui.BeginDisabled(!canEdit);
                Gui.TableSetColumnIndex(0);
                if (Gui.Checkbox("##Enabled", ref enabled))
                {
                    var next = enabled;
                    pendingCollectionAction = () => owner.ChangeModelMaterial(field, slot, model, null, next);
                }
                Gui.EndDisabled();
                Gui.SameLine(); Gui.AlignTextToFramePadding();
                Gui.TextUnformatted(descriptor.Name ?? index.ToString(CultureInfo.InvariantCulture));
                Gui.TableSetColumnIndex(1);
                Gui.SetNextItemWidth(-1);
                var material = owner.ReadModelMaterial(field, index, model);
                ReferencePicker.DrawInspector(typeof(Material), material, false,
                    reference => referencePhase.Submit(() => owner.ChangeModelMaterial(field, slot, model, (Material?)reference, null)), forceAssignment: !enabled,
                    highlighted: descriptor.Name != null && IsSceneMaterialSlotHighlighted(descriptor.Name), canEdit: canEdit);
                DrawModelMaterialContext([new(owner, field, slot, model)], [material]); Gui.EndTable(); Gui.PopID();
            }
        Gui.PopID();
    }
}
