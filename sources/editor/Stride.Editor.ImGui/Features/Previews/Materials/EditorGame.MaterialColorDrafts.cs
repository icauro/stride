using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMaterialColorDraft(AssetMetadataDocument owner, MaterialSettingField field, string label)
    {
        using var nullableRow = NativeNullableRow.Begin("##NullableMaterial", Nullable.GetUnderlyingType(field.Node.Type) != null);
        if (Nullable.GetUnderlyingType(field.Node.Type) != null)
        {
            DrawMaterialNullableSpecialPresence(owner, field); nullableRow.ValueColumn();
            if (owner.ReadMaterialSetting(field) == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        if (!label.StartsWith("##", StringComparison.Ordinal)) { Gui.TextUnformatted(label); Gui.SameLine(); }
        DrawRetainedColor(field.Node, field.Type, () => ReadMaterialSettingDraft(owner, field),
            () => CanEditMaterialSettingDraft(owner, field),
            create => CommitMaterialSettingDraft(owner, field, create(field.Node.Retrieve())),
            () => AcquireMaterialSettingDraft(owner, field),
            axis => !owner.ExtensionNumericAxisReadOnly(field.Node, axis),
            () => DrawAssetSettingResetToBase(owner, field.Node),
            Controls.Enabled ? part => Controls.Record("material-settings/" + field.Key + "/" + part) : null);
    }
}
