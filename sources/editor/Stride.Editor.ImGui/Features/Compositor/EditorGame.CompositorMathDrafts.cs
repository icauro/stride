using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsCompositorNumericVector(Type type) => type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
        || type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(UInt4);
    private static bool UsesCompositorRetainedMath(Type type) => IsCompositorNumericVector(type)
        || type == typeof(Matrix) || type == typeof(Rectangle) || type == typeof(RectangleF) || type == typeof(Quaternion);
    private void DrawCompositorRetainedMath(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var fields = target.Fields; var first = fields[0]; var type = first.Type;
        var rotation = type == typeof(Quaternion); var matrix = type == typeof(Matrix);
        var dimensions = rotation ? 3 : matrix ? 16 : type == typeof(Rectangle) || type == typeof(RectangleF) ? 4 : CommonValueDimensions(type);
        var view = Gui.GetID("##Rotation");
        var rotations = rotation ? fields.Select(field => RotationEditor(field.Accessor.Node, view, field.Accessor.IsItem ? field.ItemId.ToString() : "")).ToArray() : null;
        if (!Gui.BeginTable(IsCompositorNumericVector(type) ? "##Vector" : "##Coordinates", matrix ? 4 : dimensions, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < dimensions; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var index = axis;
                    var scalarType = type == typeof(UInt4) ? typeof(uint)
                        : type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Rectangle) ? typeof(int) : typeof(float);
                    object? Coordinate(object? raw, int fieldIndex) => raw == null ? null : rotations != null
                        ? rotations[fieldIndex].Read((Quaternion)raw)[index] : CompositorCoordinate(raw, index);
                    var values = target.Read(); var coordinate = Coordinate(values[0], 0);
                    var mixed = values.Take(fields.Length).Select((value, position) => !Equals(Coordinate(value, position), coordinate)).Any(different => different);
                    if (!matrix)
                    {
                        Gui.TextUnformatted(type == typeof(Rectangle) || type == typeof(RectangleF) ? CompositorRectangleCoordinates[index] : "XYZW"[index].ToString());
                        Gui.SameLine();
                    }
                    Gui.SetNextItemWidth(-1);
                    PropertyNumericRange? Range() => rotation ? retainedAngleRange
                        : NativeNumericInput.Merge(scalarType, fields.Select(field => source.CompositorAxisRanges(field).ElementAtOrDefault(index)));
                    bool Editable() => target.Editable() && target.Read().Take(fields.Length).All(value => value != null)
                        && (rotation || fields.All(field => !source.CompositorAxisReadOnly(field, index)));
                    var feedback = Gui.GetID("##Coordinate");
                    try
                    {
                        PropertyGrid.Strings.Draw(first.Accessor.Node, mixed, Editable, target.Read, text =>
                        {
                            var number = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, scalarType, Range()));
                            if (number is float floating && !float.IsFinite(floating)) throw new ArgumentException("The coordinate must be finite.");
                            var snapshots = rotations?.Select(state => state.Capture()).ToArray();
                            try
                            {
                                target.Apply(field =>
                                {
                                    var position = Array.IndexOf(fields, field); var current = source.CompositorPropertyValue(field);
                                    if (current == null) throw new InvalidOperationException("The property no longer supplies a coordinate target.");
                                    return rotations != null ? rotations[position].ReplaceAxis((Quaternion)current, index, (float)number)
                                        : WithCompositorCoordinate(current, type, index, number);
                                });
                            }
                            catch
                            {
                                if (snapshots != null) for (var position = 0; position < snapshots.Length; position++) rotations![position].Restore(snapshots[position]);
                                throw;
                            }
                        }, target.Acquire,
                            step: text => Editable() && NativeNumericInput.StepText(scalarType, Coordinate(target.Read()[0], 0), mixed, Range(), ref text) ? text : null,
                            format: raw => NativeNumericText.Format(Coordinate(raw, 0), scalarType, Range()), controlLabel: "##Coordinate");
                    }
                    catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                    PropertyConversionFeedback.Draw(feedback);
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
