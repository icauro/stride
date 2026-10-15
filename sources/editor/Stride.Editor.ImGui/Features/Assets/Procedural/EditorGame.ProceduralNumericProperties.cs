using System.Globalization;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static string ProceduralNumberText(object? value) => value switch
    {
        float single => single.ToString("G9", CultureInfo.InvariantCulture),
        double number => number.ToString("G17", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
    private void DrawProceduralNumber(ProceduralAssetDocument asset, AssetPropertyField field, int axis = -1)
    {
        var names = NativeNumericAxes.Names(field.Type);
        var descriptor = axis < 0 ? null : ((ObjectDescriptor)TypeDescriptorFactory.Default.Find(field.Type)).Members.First(member => member.Name == names[axis]);
        var type = descriptor?.Type ?? field.Type;
        object? Read() => descriptor == null ? field.Node.Retrieve() : descriptor.Get(field.Node.Retrieve()!);
        var range = axis < 0 ? asset.NumericRange(field.Node) : asset.AxisNumericRanges(field.Node).ElementAtOrDefault(axis);
        if (axis >= 0 && type == typeof(float) && range?.DecimalPlaces == null)
            range = new PropertyNumericRange(new PropertyNumericMetadata(range?.Minimum, range?.Maximum,
                range?.DeclaredSmallStep, range?.DeclaredLargeStep, 3), type);
        bool Editable() => !IsAuthoringPipelineUpdating && IsCurrentProceduralControlOwner(asset) && asset.IsPropertyFieldCurrent(field) && field.SupportsEditing
            && asset.CanEditAssetSettingNode(field.Node) && !asset.NumericReadOnly(field.Node, axis);
        object?[] Baseline() => [Read(), field.Node.Retrieve(), field.Node, field.Node.Parent.Retrieve(), field.Node.Type, field.Name,
            asset.Asset.Type, asset.Path, asset.EditRevision, asset.SessionHistory?.Extensions?.Version,
            sessionHistory?.Registry.Find(((IAssetTagDocument)asset).AuthoredAsset.Id)?.Generation];
        var ranged = axis < 0 && NativeRangedNumericControl.Matches(field.Node.Type, range);
        var initial = field.Node.Retrieve(); object? display = initial; var sliderCommit = false; var sliderActive = false;
        Gui.SetNextItemWidth(-1);
        try
        {
            if (ranged)
            {
                using var sliderDisabled = ImGuiScopes.Disabled(!Editable());
                sliderCommit = NativeRangedNumericControl.Draw(field.Node, type, initial, false, [range], UiScale(1), out display, out _);
                sliderActive = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, Editable, Baseline, text =>
            {
                var number = NativeNumericText.Parse(text, type, range);
                if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                    throw new FormatException("The numeric value must be finite.");
                number = range?.Normalize(number) ?? number;
                var previous = field.Node.Retrieve();
                object? proposed = number;
                if (descriptor != null)
                {
                    proposed = System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(previous!);
                    descriptor.Set(proposed, number);
                }
                if (Equals(previous, proposed)) return;
                asset.EndEdit();
                ((IEditorAssetGraphDocument)asset).ApplyInheritedAssetMutation(() =>
                {
                    if (!Editable()) throw new InvalidOperationException("The procedural coordinate is no longer editable.");
                    field.Node.Update(proposed);
                }, "Edit " + field.Label);
                assetEditError = null;
            }, () => AcquireRetainedProceduralNumber(asset, () => asset.AcquireExtensionCommandTarget(field.Node)), text =>
            {
                if (type == typeof(decimal) && !ranged) return null;
                var stepped = text;
                return NativeNumericInput.StepText(type, Read(), false, range, ref stepped) ? stepped : null;
            }, format: value => NativeNumericText.Format(value, type, range),
                externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, type, range) : null,
                externalActive: sliderActive, externalCommit: sliderCommit);
        }
        catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
        if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name + (axis < 0 ? "" : "/axis/" + names[axis]));
    }
}
