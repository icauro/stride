using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private readonly ref struct PropertyRowScope
    {
        internal bool DrawContents { get; }
        internal PropertyRowScope(string id, string label) { Gui.PushID(id); DrawContents = PropertyGridRows.BeginRow(label); }
        public void Dispose() { if (DrawContents) Gui.EndTable(); Gui.PopID(); }
    }
    private readonly ref struct MixedValueScope
    {
        internal MixedValueScope(bool mixed) => Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
        public void Dispose() => Gui.PopItemFlag();
    }
    private const string DifferentValues = "(Different values)";
    private string PropertyDraftKey(string path) => string.Join(",", selected.OrderBy(value => value)) + "/" + path;
    private void DrawMixedScalar(UIElementProperty field, object value, bool canEdit)
    {
        var key = PropertyDraftKey(field.Name);
        var text = drafts.GetValueOrDefault(key, canEdit ? "" : DifferentValues);
        var enter = Gui.InputTextWithHint("##Value", DifferentValues, ref text, 64,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
        var stepped = false;
        if (canEdit && NativeNumericInput.TryGesture(field.Type, null, out _))
        {
            var fields = selected.Select(id => document!.Properties(id).Single(other => other.Name == field.Name)).ToArray();
            stepped = NativeNumericInput.StepText(field.Type, null, true,
                NativeNumericInput.Merge(field.Type, fields.Select(other => document!.ExtensionNumericRange(other.Node) ?? other.NumericRange)), ref text);
            enter |= stepped;
        }
        var feedback = Gui.GetID("##Value"); PropertyConversionFeedback.Draw(feedback);
        if (canEdit && (Gui.IsItemEdited() || stepped)) drafts[key] = text;
        if (!canEdit || !(enter || Gui.IsItemDeactivatedAfterEdit()) || !drafts.ContainsKey(key)) return;
        try
        {
            var replacement = value is int ? (object)int.Parse(text, CultureInfo.InvariantCulture) : double.Parse(text, CultureInfo.InvariantCulture);
            document!.SetProperties(selected.ToArray(), field.Name, replacement); drafts.Remove(key); PropertyConversionFeedback.Validate(feedback);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportValidationFailure(error); }
    }
    private void DrawCoordinateProperties(UIElementProperty field, object value, bool canEdit)
    {
        var source = document!; var ids = selected.ToArray();
        var fields = ids.Select(id => source.Properties(id).Single(other => other.Name == field.Name)).ToArray();
        var target = MemberValueTarget(source, ids, field.Name);
        DrawUICoordinates(field.Name, value, fields.Select(other => other.Node.Retrieve()).ToArray(), canEdit,
            target.Transform,
            axis => NativeNumericInput.Merge(typeof(float), fields.Select(other => source.UIAxisNumericRanges(other.Node).ElementAtOrDefault(axis))),
            axis => fields.All(other => !source.IsUIAxisReadOnly(other.Node, axis)), target);
    }
    private static float Coordinate(object value, int axis) => value switch
    {
        Vector2 item => axis == 0 ? item.X : item.Y,
        Vector3 item => axis == 0 ? item.X : axis == 1 ? item.Y : item.Z,
        Vector4 item => axis == 0 ? item.X : axis == 1 ? item.Y : axis == 2 ? item.Z : item.W,
        Thickness item => axis == 0 ? item.Left : axis == 1 ? item.Right : axis == 2 ? item.Top : axis == 3 ? item.Bottom : axis == 4 ? item.Back : item.Front,
        _ => throw new InvalidOperationException("The UI property has no numeric coordinates."),
    };
    private static object WithCoordinate(object value, int axis, float coordinate)
    {
        switch (value)
        {
            case Vector2 item: if (axis == 0) item.X = coordinate; else item.Y = coordinate; return item;
            case Vector3 item: if (axis == 0) item.X = coordinate; else if (axis == 1) item.Y = coordinate; else item.Z = coordinate; return item;
            case Vector4 item: if (axis == 0) item.X = coordinate; else if (axis == 1) item.Y = coordinate; else if (axis == 2) item.Z = coordinate; else item.W = coordinate; return item;
            case Thickness item:
                if (axis == 0) item.Left = coordinate; else if (axis == 1) item.Right = coordinate; else if (axis == 2) item.Top = coordinate;
                else if (axis == 3) item.Bottom = coordinate; else if (axis == 4) item.Back = coordinate; else item.Front = coordinate;
                return item;
            default: throw new InvalidOperationException("The UI property has no numeric coordinates.");
        }
    }
}
