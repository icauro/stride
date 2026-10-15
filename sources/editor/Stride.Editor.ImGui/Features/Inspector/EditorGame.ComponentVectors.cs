using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentVector(ComponentField field)
    {
        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return; }

        var available = Gui.GetContentRegionAvail().X;
        var minimumAxisWidth = Gui.CalcTextSize("X0.00").X + Gui.GetStyle().FramePadding.X * 4 + Gui.GetStyle().CellPadding.X * 2;
        var columns = Math.Clamp((int)(available / Math.Max(1, minimumAxisWidth)), 1, field.VectorDimensions);
        if (Gui.BeginTable("##Axes", columns, Hexa.NET.ImGui.ImGuiTableFlags.SizingStretchSame))
        {
            for (var start = 0; start < field.VectorDimensions; start += columns)
            {
                Gui.TableNextRow();
                for (var axis = start; axis < Math.Min(start + columns, field.VectorDimensions); axis++)
                {
                    Gui.TableSetColumnIndex(axis - start); Gui.PushID(axis); DrawInspectorAxisBadge(axis); Gui.SetNextItemWidth(-1);
                    DrawComponentCoordinateDraft(field, axis, "##Axis");
                    // A partially clipped widget can be visible while its click center is outside the window.
                    if (Gui.IsItemVisible() && Gui.GetItemRectMax().Y <= Gui.GetWindowPos().Y + Gui.GetWindowSize().Y - Gui.GetStyle().WindowPadding.Y)
                        if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/axis/" + "XYZW"[axis]);
                    Gui.PopID();
                }
            }
            Gui.EndTable();
        }
        if (componentErrors.TryGetValue(field, out var message)) DrawSelectableText("##VectorError", message);
        Gui.EndTable(); Gui.PopID();
    }
}
