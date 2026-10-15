namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal bool CanUpdateRegisteredProperty(AssetPropertyField field)
        => CanEdit && IsPropertyFieldCurrent(field) && SessionHistory?.Extensions?.HasPrimitiveEditor(field.Node.Type) == true
        && AssetMetadataDocument.CanAssignAuthoringNode(field.Node) && CanEditAssetSettingNode(field.Node) && !NumericReadOnly(field.Node);
    internal void UpdateRegisteredProperty(AssetPropertyField field, object? value)
    {
        if (!CanUpdateRegisteredProperty(field)) throw new InvalidOperationException("The registered procedural property is read-only.");
        if (value == null ? !field.AllowsNull : !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The registered value has an incompatible type.");
        value = PropertyValueValidation.Normalize(value, NumericRange(field.Node));
        BeginEdit(field);
        try { field.Node.Update(value); }
        catch { CancelEdit(); throw; }
    }
    internal void EndRegisteredPropertyEdit() { EndEdit(); RefreshFields(); }
}
