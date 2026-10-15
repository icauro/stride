using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector3 = Stride.Core.Mathematics.Vector3;

namespace Stride.Editor.ImGui;

internal sealed partial class PropertyGridPanel
{
    internal bool DrawSceneRootProperties(SceneDocument? assetOwner = null)
    {
        if ((assetOwner ?? host.SelectedSceneRoot) is not { } owner) return false;
        var node = owner.SceneOffsetNode;
        var ranges = VectorPropertyNumericMetadata.Create(node);
        var style = Gui.GetStyle();
        var available = PropertyGridRows.AvailableWidth();
        var labelWidth = PropertyGridRows.LabelWidth(available);
        var axisWidth = Gui.CalcTextSize("X").X + style.ItemInnerSpacing.X + Gui.CalcTextSize("0.00").X
            + style.FramePadding.X * 4 + style.CellPadding.X * 2;
        var inline = available >= labelWidth + style.CellPadding.X * 2 + axisWidth * 3;
        var columns = inline ? 3 : Math.Clamp((int)(available / Math.Max(1, axisWidth)), 1, 3);
        if (!inline) Gui.TextUnformatted("Offset");
        if (!Gui.BeginTable("##SceneOffset", columns + (inline ? 1 : 0), ImGuiTableFlags.SizingStretchProp, new System.Numerics.Vector2(available, 0))) return true;
        if (inline) Gui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, labelWidth);
        for (var axis = 0; axis < columns; axis++) Gui.TableSetupColumn("XYZ"[axis].ToString(), ImGuiTableColumnFlags.WidthStretch, 1);
        Gui.TableNextRow();
        if (inline) { Gui.TableSetColumnIndex(0); Gui.AlignTextToFramePadding(); Gui.TextUnformatted("Offset"); }
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis > 0 && axis % columns == 0) Gui.TableNextRow();
            Gui.TableSetColumnIndex(axis % columns + (inline ? 1 : 0));
            var coordinate = axis;
            bool Editable() => host.IsCurrentScenePropertyOwner(owner) && owner.CanEditSceneOffset(node);
            using var disabled = ImGuiScopes.Disabled(!Editable());
            PropertyGridRows.DrawAxisBadge(axis); Gui.SetNextItemWidth(-1);
            var control = "##Offset/" + axis; var feedback = Gui.GetID(control);
            float ReadCoordinate(object? value) => value is Vector3 vector ? vector[coordinate]
                : throw new InvalidOperationException("The scene offset type changed.");
            string Format(object? value)
            {
                var places = ranges.ElementAtOrDefault(coordinate)?.DecimalPlaces ?? 3;
                return ReadCoordinate(value).ToString(places <= 0 ? "0" : "0." + new string('#', Math.Clamp(places, 0, 15)), CultureInfo.InvariantCulture);
            }
            try
            {
                Strings.Draw(node, false, Editable, () => [node.Retrieve()], text =>
                {
                    var number = PropertyConversionFeedback.Convert(feedback,
                        () => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                    owner.SetSceneOffsetCoordinate(node, coordinate, number);
                }, () => host.AcquirePropertyDraftOwner(owner), text =>
                    NativeNumericInput.StepText(typeof(float), ReadCoordinate(node.Retrieve()), false,
                        ranges.ElementAtOrDefault(coordinate), ref text) ? text : null,
                    Format, controlLabel: control);
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportScenePropertyFailure(error); }
            PropertyConversionFeedback.Draw(feedback);
            if (host.Controls.Enabled) host.Controls.Record("Offset/" + axis);
        }
        Gui.EndTable();
        return true;
    }
}
