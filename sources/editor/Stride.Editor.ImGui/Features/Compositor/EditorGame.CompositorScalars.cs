using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class CompositorScalarText { public string Text = ""; public object? Value; }
    private readonly ConditionalWeakTable<CompositorProperty, CompositorScalarText> compositorScalarTexts = new();
    private static bool IsCompositorExtraScalarType(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
        || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(UInt4)
        || type == typeof(Color) || type == typeof(Rectangle) || type == typeof(RectangleF) || type == typeof(Quaternion) || type == typeof(TimeSpan)
        || type == typeof(decimal) || type == typeof(char) || type == typeof(Guid) || type == typeof(Matrix)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(UFile) || type == typeof(UDirectory) || type == typeof(AngleSingle);
    private unsafe bool DrawCompositorExtraScalar(AssetMetadataDocument document, CompositorProperty property, object? value, out object? updated, out bool deactivated)
    {
        updated = value; deactivated = false; var changed = false;
        if (!IsCompositorNumericVector(property.Type) && property.Type != typeof(Rectangle) && property.Type != typeof(RectangleF)
            && property.Type != typeof(Matrix) && property.Type != typeof(Quaternion) && property.Type != typeof(TimeSpan) && property.Type != typeof(char)) DrawCompositorLabel(property);
        if (property.Type == typeof(byte)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.U8, (byte)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(sbyte)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.S8, (sbyte)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(short)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.S16, (short)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(ushort)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.U16, (ushort)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(uint)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.U32, (uint)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(long)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.S64, (long)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(ulong)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.U64, (ulong)value!, property.Range, out var v); updated = v; }
        else if (property.Type == typeof(decimal) || property.Type == typeof(DateTimeOffset))
        {
            var text = compositorScalarTexts.GetValue(property, static _ => new());
            if (!Equals(text.Value, value)) { text.Value = value; text.Text = value is DateTimeOffset offset ? offset.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
            var accepted = Gui.InputText("##Value", ref text.Text, 512, ImGuiInputTextFlags.EnterReturnsTrue);
            var feedback = Gui.GetID("##Value");
            deactivated = Gui.IsItemDeactivatedAfterEdit();
            if (property.Type == typeof(decimal) && TryCompositorNumericGesture(typeof(decimal), property.Range, out var amount))
            {
                try
                {
                    var baseline = decimal.Parse(text.Text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                    updated = StepCompositorNumber(typeof(decimal), baseline, property.Range, amount);
                    text.Text = Convert.ToString(updated, CultureInfo.InvariantCulture) ?? "";
                    changed = true; deactivated = true; PropertyConversionFeedback.Validate(feedback);
                }
                catch (Exception error) when (error is FormatException or OverflowException) { PropertyConversionFeedback.Fail(feedback); }
            }
            if (accepted || deactivated)
                try
                {
                    updated = property.Type == typeof(DateTimeOffset) ? DateTimeOffset.Parse(text.Text, CultureInfo.InvariantCulture, DateTimeStyles.None)
                        : decimal.Parse(text.Text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                    changed = true; PropertyConversionFeedback.Validate(feedback);
                }
                catch (Exception error) when (error is FormatException or OverflowException) { PropertyConversionFeedback.Fail(feedback); }
            PropertyConversionFeedback.Draw(feedback);
        }
        else return false;
        deactivated |= Gui.IsItemDeactivatedAfterEdit(); return changed;
    }
}
