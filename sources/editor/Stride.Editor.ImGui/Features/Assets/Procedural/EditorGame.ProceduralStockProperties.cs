using System.Globalization;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanEditProceduralProperty(ProceduralAssetDocument asset, AssetPropertyField field)
        => ReferenceEquals(assetDocument, asset) && asset.IsPropertyFieldCurrent(field) && field.SupportsEditing
            && asset.CanEditAssetSettingNode(field.Node) && !asset.NumericReadOnly(field.Node);
    private void CommitProceduralProperty(ProceduralAssetDocument asset, AssetPropertyField field, object? expected, object? value, bool validateDraft = true)
    {
        if (validateDraft) ValidatePendingPropertyStrings();
        if (!CanEditProceduralProperty(asset, field) || !ContentAssetReference.AreEqual(expected, field.Node.Retrieve())
            || value == null && !field.AllowsNull || value != null && !field.Type.IsInstanceOfType(value))
            throw new InvalidOperationException("The procedural property changed before validation.");
        if (ContentAssetReference.AreEqual(expected, value)) return;
        asset.EndEdit();
        ((IEditorAssetGraphDocument)asset).ApplyInheritedAssetMutation(() => field.Node.Update(value), "Edit " + field.Label);
        assetEditError = null;
    }
    private bool DrawProceduralStockProperty(ProceduralAssetDocument asset, AssetPropertyField field)
    {
        var value = field.Node.Retrieve(); var type = field.Type;
        if (type == typeof(UFile) || type == typeof(UDirectory))
        {
            DrawTypedPropertyPath(asset, type, value, () => CanEditProceduralProperty(asset, field),
                () => field.Node.Retrieve(), path => CommitProceduralProperty(asset, field, field.Node.Retrieve(), path), identity: field.Node);
        }
        else if (ContentAssetReference.Supports(type))
        {
            DrawContentReferenceValue(type, value, field.AllowsNull,
                reference => CommitProceduralProperty(asset, field, value, reference), canEdit: CanEditProceduralProperty(asset, field));
        }
        else if (IsTemporalType(type))
        {
            DrawRetainedTemporal(field.Node, type, () => CanEditProceduralProperty(asset, field), () => [field.Node.Retrieve()],
                transform => CommitProceduralProperty(asset, field, field.Node.Retrieve(), transform(field.Node.Retrieve())),
                1, () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(char))
        {
            DrawRetainedCharacter(field.Node, () => [field.Node.Retrieve()], () => CanEditProceduralProperty(asset, field),
                character => CommitProceduralProperty(asset, field, field.Node.Retrieve(), character), () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(Guid))
        {
            DrawRetainedGuid(field.Node, () => [field.Node.Retrieve()], () => CanEditProceduralProperty(asset, field),
                guid => CommitProceduralProperty(asset, field, field.Node.Retrieve(), guid), () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(AngleSingle))
        {
            DrawRetainedAngle(field.Node, () => [field.Node.Retrieve()], () => CanEditProceduralProperty(asset, field),
                angle => CommitProceduralProperty(asset, field, field.Node.Retrieve(), angle), () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4))
        {
            DrawRetainedColor(field.Node, type, () => [field.Node.Retrieve()], () => CanEditProceduralProperty(asset, field),
                create => CommitProceduralProperty(asset, field, field.Node.Retrieve(), create(field.Node.Retrieve())),
                () => AcquirePropertyStringOwner(asset), axis => !asset.NumericReadOnly(field.Node, axis));
        }
        else if (type == typeof(Quaternion)) DrawProceduralRotation(asset, field);
        else return false;
        if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
        return true;
    }
    private void DrawReadOnlyProceduralColor(object? value)
    {
        var color = value is Color packed ? packed.ToColor4() : value is Color3 rgb ? rgb.ToColor4() : value as Color4? ?? Color4.Black;
        Gui.ColorButton("##Swatch", new System.Numerics.Vector4(color.R, color.G, color.B, color.A), ImGuiColorEditFlags.NoTooltip);
        Gui.SameLine(); var text = ColorHex(value);
        NativeStringInput.Draw("##Hex", ref text, ImGuiInputTextFlags.ReadOnly);
    }
    private void DrawProceduralRotation(ProceduralAssetDocument asset, AssetPropertyField field)
    {
        var rotation = RotationEditor(field.Node, Gui.GetID("##Rotation"));
        if (!Gui.BeginTable("##Rotation", 3, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    DrawInspectorAxisBadge(axis); Gui.SetNextItemWidth(-1);
                    var coordinate = axis;
                    Quaternion Read() => (Quaternion)field.Node.Retrieve()!;
                    object?[] Baseline()
                    {
                        var value = Read();
                        return [ProceduralNumberText(rotation.Read(value)[coordinate]), ProceduralNumberText(value.X),
                            ProceduralNumberText(value.Y), ProceduralNumberText(value.Z), ProceduralNumberText(value.W)];
                    }
                    PropertyGrid.Strings.Draw(field.Node, false, () => CanEditProceduralProperty(asset, field), Baseline, text =>
                    {
                        var degrees = (float)NativeNumericText.Parse(text, typeof(float));
                        if (!float.IsFinite(degrees)) throw new FormatException("The rotation value must be finite.");
                        var previous = Read();
                        CommitProceduralProperty(asset, field, previous, rotation.ReplaceAxis(previous, coordinate, degrees), validateDraft: false);
                    }, () => AcquirePropertyStringOwner(asset), text =>
                    {
                        var stepped = text;
                        return NativeNumericInput.StepText(typeof(float), rotation.Read(Read())[coordinate], false, null, ref stepped) ? stepped : null;
                    });
                    if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name + "/axis/" + "XYZ"[axis]);
                }
                catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
