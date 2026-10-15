using System.Globalization;
using Stride.Core.Mathematics;
using Stride.Core;
using Stride.Core.Reflection;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralCollectionEnum(ProceduralAssetDocument asset, ProceduralCollectionItem item)
    {
        var value = (Enum)(item.Value ?? Enum.ToObject(item.Type, 0));
        var flags = item.Type.IsDefined(typeof(FlagsAttribute), true);
        var options = Enum.GetNames(item.Type).Select(name => (Name: name, Value: (Enum)Enum.Parse(item.Type, name),
            Label: TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(item.Type.GetField(name)!)?.Name ?? name)).ToArray();
        void Commit(Enum next)
        {
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset)) throw new InvalidOperationException("The selected procedural document changed.");
            asset.UpdateCollectionItem(item, value, next); value = next;
        }
        using var disabled = ImGuiScopes.Disabled(!asset.CanEditCollectionItem(item));
        if (icons?.HasEnumImages(item.Type) == true)
        {
            if (NativeEnumImageControl.Draw(icons, item.Context.Node, item.Type, value, false, options.Select(option => option.Value), UiScale(1), out var next)) Execute(() => Commit((Enum)next!));
        }
        else if (BeginNativeCombo("##Value", value.ToString()))
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
                            var bits = EnumBits(value); var mask = EnumBits(option.Value); var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                            if (DrawNativeCheckbox(option.Label, ref selected)) Execute(() => Commit((Enum)EnumFromBits(item.Type, mask == 0 ? 0 : selected ? bits | mask : bits & ~mask)));
                        }
                        else if (DrawNativeSelectable(option.Label, Equals(value, option.Value))) Execute(() => Commit(option.Value));
                    }
                    finally { Gui.PopID(); }
                }
                if (flags && DrawFlagBulkActions() is { } action) Execute(() => Commit(ApplyFlagBulkAction(value, action)));
            }
            finally { Gui.EndCombo(); }
        }
    }
    private void DrawProceduralCollectionRotation(ProceduralAssetDocument asset, ProceduralCollectionItem item)
    {
        var state = RotationEditor(item.Context.Node, Gui.GetID("##Rotation"), item.Key);
        if (!Gui.BeginTable("##Rotation", 3, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    DrawInspectorAxisBadge(axis); Gui.SetNextItemWidth(-1); var coordinate = axis;
                    Quaternion Read() => (Quaternion)item.Value!;
                    object?[] Baseline()
                    {
                        var value = Read();
                        return [ProceduralNumberText(state.Read(value)[coordinate]), ProceduralNumberText(value.X), ProceduralNumberText(value.Y), ProceduralNumberText(value.Z), ProceduralNumberText(value.W)];
                    }
                    PropertyGrid.Strings.Draw(item.Context.Node, false,
                        () => ReferenceEquals(assetDocument, asset) && asset.CanEditCollectionItem(item), Baseline, text =>
                        {
                            var degrees = (float)NativeNumericText.Parse(text, typeof(float));
                            if (!float.IsFinite(degrees)) throw new FormatException("The rotation value must be finite.");
                            var previous = Read(); asset.UpdateCollectionItem(item, previous, state.ReplaceAxis(previous, coordinate, degrees));
                        }, () => AcquirePropertyStringOwner(asset), text =>
                        {
                            var stepped = text;
                            return NativeNumericInput.StepText(typeof(float), state.Read(Read())[coordinate], false, null, ref stepped) ? stepped : null;
                        });
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
