using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsImportMathType(Type type) => type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
        || type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Quaternion)
        || type == typeof(Matrix) || type == typeof(Rectangle) || type == typeof(RectangleF);
    private void DrawImportMathDraft(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        var ranges = owner.ExtensionAxisNumericRanges(field.Node);
        DrawRetainedImportMath(field.Node, field.Type, () => ReadImportSettingDraft(owner, field),
            () => CanEditImportSettingDraft(owner, field), value => CommitImportSettingDraft(owner, field, value),
            () => AcquireImportSettingDraft(owner, field), axis => ranges.ElementAtOrDefault(axis),
            axis => owner.ExtensionNumericAxisReadOnly(field.Node, axis),
            part => { if (Controls.Enabled) Controls.Record("asset-settings/" + field.Key + "/" + part); });
        if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + field.Key);
    }
    private void DrawRetainedImportMath(object identity, Type type, Func<object?[]> read, Func<bool> canEdit,
        Action<object?> assign, Func<IDisposable> acquire, Func<int, PropertyNumericRange?> rangeAt,
        Func<int, bool> axisReadOnly, Action<string>? capture = null)
    {
        // A null parent has no actual coordinates; its native factory/presence
        // route must create it before this coordinate editor can author it.
        if (read()[0] == null) return;
        var rotation = type == typeof(Quaternion) ? RotationEditor(identity, Gui.GetID("##Rotation")) : null;
        var count = rotation != null ? 3 : CommonValueDimensions(type);
        var columns = UsesExtendedMathTable(type) ? 4 : count;
        if (!Gui.BeginTable("##Vector", columns, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < columns; axis++) Gui.TableSetupColumn("##" + axis, ImGuiTableColumnFlags.WidthStretch, 1f);
            for (var axis = 0; axis < count; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    if (UsesExtendedMathTable(type))
                    {
                        if (type != typeof(Matrix)) { Gui.TextUnformatted(ExtendedMathLabel(type, axis)); Gui.SameLine(); }
                    }
                    else DrawInspectorAxisBadge(axis);
                    Gui.SetNextItemWidth(-1);
                    DrawAxis(axis);
                    capture?.Invoke("axis/" + ExtendedMathLabel(type, axis));
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
        void DrawAxis(int axis)
        {
            var scalarType = PropertyCoordinateType(type, axis);
            var range = rotation == null ? rangeAt(axis) : null;
            if (scalarType == typeof(float) && range?.DecimalPlaces == null)
                range = new PropertyNumericRange(new PropertyNumericMetadata(range?.Minimum, range?.Maximum,
                    range?.DeclaredSmallStep, range?.DeclaredLargeStep, 3), scalarType);
            object? Coordinate(object? value) => rotation != null && value is Quaternion authored
                ? rotation.Read(authored)[axis] : CommonCoordinate(value, axis);
            bool Editable() => !IsAuthoringPipelineUpdating && canEdit() && read()[0] != null && (rotation != null || !axisReadOnly(axis));
            var feedback = Gui.GetID("##Axis");
            try
            {
                PropertyGrid.Strings.Draw(identity, false, Editable, read, text =>
                {
                    var snapshot = rotation?.Capture();
                    try
                    {
                        var number = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, scalarType, range));
                        if (number is float single && !float.IsFinite(single)) throw new FormatException("The coordinate must be finite.");
                        var previous = read()[0];
                        var proposed = rotation != null && previous is Quaternion quaternion
                            ? rotation.ReplaceAxis(quaternion, axis, (float)number) : WithCommonCoordinate(previous, type, axis, number);
                        assign(proposed);
                    }
                    catch { if (snapshot != null) rotation!.Restore(snapshot); throw; }
                }, acquire, text =>
                {
                    var stepped = text;
                    return NativeNumericInput.StepText(scalarType, Coordinate(read()[0]), false, range, ref stepped) ? stepped : null;
                }, format: value => NativeNumericText.Format(Coordinate(value), scalarType, range), controlLabel: "##Axis",
                    onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
            PropertyConversionFeedback.Draw(feedback);
        }
    }
}
