using Stride.Core;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralEnum(ProceduralAssetDocument asset, AssetPropertyField field)
    {
        bool Editable() => ReferenceEquals(assetDocument, asset) && asset.IsPropertyFieldCurrent(field)
            && field.SupportsEditing && asset.CanEditAssetSettingNode(field.Node) && !asset.NumericReadOnly(field.Node);
        var value = (Enum)(field.Node.Retrieve() ?? Enum.ToObject(field.Type, 0));
        var options = field.EnumNames.Select(name =>
        {
            var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(field.Type.GetField(name)!);
            return (Name: name, Label: display?.Name ?? name, Value: (Enum)Enum.Parse(field.Type, name));
        }).ToArray();
        void Commit(Enum next)
        {
            ValidatePendingPropertyStrings();
            if (!Editable() || !Equals(value, field.Node.Retrieve()))
                throw new InvalidOperationException("The procedural enum property changed before validation.");
            asset.EndEdit();
            ((IEditorAssetGraphDocument)asset).ApplyInheritedAssetMutation(() => field.Node.Update(next), "Edit " + field.Label);
            value = next; assetEditError = null;
        }
        var flags = field.Type.IsDefined(typeof(FlagsAttribute), true);
        using var disabled = ImGuiScopes.Disabled(!Editable());
        Gui.SetNextItemWidth(-1);
        try
        {
            if (icons?.HasEnumImages(field.Type) == true)
            {
                if (NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, false,
                    options.Select(option => option.Value), UiScale(1), out var next)) Commit((Enum)next!);
                if (flags && Gui.BeginPopupContextItem("Flag actions"))
                {
                    try { if (DrawFlagBulkActions() is { } action) Commit(ApplyFlagBulkAction(value, action)); }
                    finally { Gui.EndPopup(); }
                }
            }
            else if (BeginNativeCombo("##Value", options.FirstOrDefault(option => Equals(value, option.Value)).Label ?? value.ToString()))
            {
                try
                {
                    foreach (var option in options)
                    {
                        Gui.PushID(option.Name);
                        try
                        {
                            if (flags)
                            {
                                var bits = EnumBits(value); var mask = EnumBits(option.Value);
                                var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                                if (DrawNativeCheckbox(option.Label, ref selected))
                                    Commit((Enum)EnumFromBits(field.Type, mask == 0 ? 0 : selected ? bits | mask : bits & ~mask));
                            }
                            else if (DrawNativeSelectable(option.Label, Equals(value, option.Value))) Commit(option.Value);
                            if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name + "/option/" + option.Name);
                        }
                        finally { Gui.PopID(); }
                    }
                    if (flags && DrawFlagBulkActions() is { } action) Commit(ApplyFlagBulkAction(value, action));
                }
                finally { Gui.EndCombo(); }
            }
        }
        catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
        if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
    }
}
