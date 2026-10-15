using System.Globalization;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedVirtualMath(object identity, Type type, IReadOnlyList<INodePresenter> nodes,
        Func<object?[]> read, Func<bool> canEdit, Func<int, IReadOnlyList<PropertyNumericRange?>> rangesFor,
        Action<Func<int, object?, object?>> apply, Func<IDisposable> acquire)
    {
        var rotation = type == typeof(Quaternion);
        var view = Gui.GetID("##Rotation");
        var states = rotation ? nodes.Select(node => RotationEditor(node, view)).ToArray() : null;
        var names = NativeNumericAxes.Names(type);
        var dimensions = rotation ? 3 : names.Count;
        var tableName = rotation ? "##Rotation" : "##Components";
        if (!Gui.BeginTable(tableName, type == typeof(Matrix) ? 4 : dimensions, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < dimensions; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var coordinate = axis;
                    object? Coordinate(object? value, int owner = 0) => rotation
                        ? value is Quaternion quaternion ? states![owner].Read(quaternion)[coordinate] : null
                        : ReadVirtualMathAxis(value, coordinate);
                    var values = read(); var number = Coordinate(values[0]);
                    var mixed = values.Take(nodes.Count).Select((value, owner) => Coordinate(value, owner)).Any(value => !Equals(value, number));
                    var scalarType = number?.GetType() ?? (rotation ? typeof(float) : ReadVirtualMathAxis(Activator.CreateInstance(type), coordinate)!.GetType());
                    var ranges = rangesFor(coordinate); var range = NativeNumericInput.Combine(scalarType, ranges);
                    bool Editable() => !IsAuthoringPipelineUpdating && canEdit()
                        && nodes.All(node => !NativeNumericAxes.IsReadOnly(node, coordinate));
                    if (rotation || names[axis] is "X" or "Y" or "Z" or "W") DrawInspectorAxisBadge(axis);
                    Gui.SetNextItemWidth(-1); var feedback = Gui.GetID("##Axis");
                    var snapshots = states?.Select(state => state.Capture()).ToArray();
                    try
                    {
                        PropertyGrid.Strings.Draw(identity, mixed, Editable, read, text =>
                        {
                            var parsed = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, scalarType, range));
                            try
                            {
                                apply((owner, current) => rotation
                                    ? states![owner].ReplaceAxis(current is Quaternion quaternion ? quaternion : Quaternion.Identity,
                                        coordinate, Convert.ToSingle(parsed, CultureInfo.InvariantCulture))
                                    : ReplaceVirtualMathAxis(current ?? Activator.CreateInstance(type)!, coordinate,
                                        Convert.ToDouble(parsed, CultureInfo.InvariantCulture)));
                            }
                            catch
                            {
                                if (snapshots != null) for (var owner = 0; owner < snapshots.Length; owner++) states![owner].Restore(snapshots[owner]);
                                throw;
                            }
                        }, acquire,
                            step: text => NativeNumericInput.StepText(scalarType, Coordinate(read()[0]), mixed, range, ref text) ? text : null,
                            format: value => Coordinate(value) is { } source
                                ? source is float or double ? Convert.ToDouble(source, CultureInfo.InvariantCulture).ToString("F" + (range?.DecimalPlaces ?? 3), CultureInfo.InvariantCulture)
                                    : NativeNumericText.Format(source, scalarType, range)
                                : "", controlLabel: "##Axis");
                    }
                    catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                    PropertyConversionFeedback.Draw(feedback);
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
    private void DrawRetainedCombinedVirtualMath(IReadOnlyList<ComponentVirtualOwner> source, Type type)
    {
        var entries = source.ToArray();
        DrawRetainedVirtualMath(entries[0].Node, type, entries.Select(entry => entry.Node).ToArray(),
            () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
            () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
            axis => entries.Select(entry => entry.Presentation.VirtualAxisNumericRanges(entry.Node).ElementAtOrDefault(axis)).ToArray(),
            transform => ApplyCombinedComponentVirtualCore(entries, (entry, value) => transform(Array.IndexOf(entries, entry), value)),
            () => AcquireRetainedCombinedVirtualTargets(entries));
    }
}
