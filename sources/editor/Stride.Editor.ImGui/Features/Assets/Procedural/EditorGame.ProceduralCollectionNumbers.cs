using System.Globalization;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralCollectionNumber(ProceduralAssetDocument asset, ProceduralCollectionItem item, int axis = -1)
    {
        var names = NativeNumericAxes.Names(item.Type);
        var descriptor = axis < 0 ? null : ((ObjectDescriptor)TypeDescriptorFactory.Default.Find(item.Type)).Members.First(member => member.Name == names[axis]);
        var type = descriptor?.Type ?? item.Type;
        var range = axis < 0 ? asset.CollectionNumericRange(item) : asset.CollectionAxisRanges(item).ElementAtOrDefault(axis);
        if (axis >= 0 && type == typeof(float) && range?.DecimalPlaces == null)
            range = new(new PropertyNumericMetadata(range?.Minimum, range?.Maximum, range?.DeclaredSmallStep, range?.DeclaredLargeStep, 3), type);
        bool Editable() => !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset) && asset.CanEditCollectionItem(item)
            && (axis < 0 || !asset.CollectionAxisReadOnly(item, axis));
        object? Read() => descriptor == null ? item.Value : descriptor.Get(item.Value!);
        object?[] Baseline()
        {
            var values = new List<object?> { Read(), item.Value, item.Context.Node, item.Index, item.Key, item.Id, asset.EditRevision };
            for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
            { values.Add(parent.Context.Node); values.Add(parent.Index); values.Add(parent.Key); values.Add(parent.Id); }
            return values.ToArray();
        }
        var ranged = axis < 0 && NativeRangedNumericControl.Matches(item.DeclaredType, range);
        var initial = item.Value; object? display = initial; var sliderCommit = false; var sliderActive = false;
        Gui.SetNextItemWidth(-1);
        if (ranged)
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            sliderCommit = NativeRangedNumericControl.Draw(item.Context.Node, type, initial, false, [range], UiScale(1), out display, out _);
            sliderActive = Gui.IsItemActive();
        }
        PropertyGrid.Strings.Draw(item.Context.Node, false, Editable, Baseline, text =>
        {
            var number = NativeNumericText.Parse(text, type, range);
            if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                throw new FormatException("The numeric value must be finite.");
            number = range?.Normalize(number) ?? number;
            var previous = item.Value; object? value = number;
            if (descriptor != null) { value = System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(previous!); descriptor.Set(value, number); }
            asset.UpdateCollectionItem(item, previous, value);
        }, () => AcquireRetainedProceduralNumber(asset, () => asset.AcquireCollectionItemTarget(item)), text =>
        {
            if (type == typeof(decimal) && !ranged) return null;
            var stepped = text;
            return NativeNumericInput.StepText(type, Read(), false, range, ref stepped) ? stepped : null;
        }, format: value => NativeNumericText.Format(value, type, range),
            externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, type, range) : null,
            externalActive: sliderActive, externalCommit: sliderCommit);
    }
}
