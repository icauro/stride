using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Engine;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class CommonMaterialGroup(ComponentField[] fields)
    {
        public ComponentField[] Fields { get; } = fields;
        public IModelAsset[] Models { get; } = new IModelAsset[fields.Length];
    }
    private void DrawCommonModelMaterials(CommonMaterialGroup group)
    {
        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
        var owner = document!;
        var fields = group.Fields; var models = group.Models;
        var count = int.MaxValue; var canEdit = true;
        for (var position = 0; position < fields.Length; position++)
        {
            var model = ModelMaterialLayout((ModelComponent)fields[position].Component);
            if (model == null) return;
            models[position] = model;
            count = Math.Min(count, model.Materials.Count);
            canEdit &= owner.CanEditModelMaterials(fields[position]);
        }
        Gui.PushID(nameof(ModelComponent.Materials)); Gui.SeparatorText(fields[0].Label);
        for (var index = 0; index < count; index++)
        {
            var slot = index;
            var enabled = owner.IsModelMaterialEnabled(fields[0], index);
            var material = owner.ReadModelMaterial(fields[0], index, models[0]);
            var enabledMixed = false; var materialMixed = false;
            for (var position = 1; position < fields.Length; position++)
            {
                enabledMixed |= owner.IsModelMaterialEnabled(fields[position], index) != enabled;
                materialMixed |= !ContentAssetReference.AreEqual(owner.ReadModelMaterial(fields[position], index, models[position]), material);
            }
            Gui.PushID(index); Gui.TextUnformatted(models[0].Materials[index].Name ?? index.ToString(CultureInfo.InvariantCulture)); Gui.SameLine();
            var slotEditable = canEdit && fields.All(field => owner.CanEditModelMaterialSlot(field, slot));
            Gui.BeginDisabled(!slotEditable);
            var value = enabled;
            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, enabledMixed);
            if (Gui.Checkbox("##Enabled", ref value))
            {
                var next = value;
                pendingCollectionAction = () => owner.ChangeCommonModelMaterials(fields, models, slot, null, next);
            }
            Gui.PopItemFlag(); Gui.EndDisabled(); Gui.SetNextItemWidth(-1);
            DrawContentReferenceValue(typeof(Material), material, false,
                reference => referencePhase.Submit(() => owner.ChangeCommonModelMaterials(fields, models, slot, (Material?)reference, null)),
                mixed: materialMixed, forceAssignment: enabledMixed || !enabled, canEdit: slotEditable);
            DrawModelMaterialContext(fields.Select((field, position) => new ModelMaterialClipboardTarget(owner, field, slot, models[position])).ToArray(),
                fields.Select((field, position) => owner.ReadModelMaterial(field, slot, models[position])));
            Gui.PopID();
        }
        Gui.PopID();
    }
}
