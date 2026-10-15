using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentQuaternion(ComponentField field)
    {

        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return; }

        var rotationState = RotationEditor(field.Node, Gui.GetID("##Rotation"));
        if (Gui.BeginTable("##Rotation", 3, ImGuiTableFlags.SizingStretchSame))
        {
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis); DrawInspectorAxisBadge(axis); Gui.SetNextItemWidth(-1);
                DrawComponentCoordinateDraft(field, axis, "##Axis", rotationState);
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/axis/" + "XYZ"[axis]);
                Gui.PopID();
            }
            Gui.EndTable();
        }
        if (componentErrors.TryGetValue(field, out var message)) DrawSelectableText("##RotationError", message);
        Gui.EndTable(); Gui.PopID();
    }

}
