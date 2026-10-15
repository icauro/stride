using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesExtendedMathTable(Type type) => type == typeof(Matrix) || type == typeof(Rectangle) || type == typeof(RectangleF);
    private static void DrawExtendedMathCoordinateLabel(Type type, int axis, bool table)
    {
        if (table) Gui.TableNextColumn();
        if (type == typeof(Matrix)) return;
        Gui.TextUnformatted(ExtendedMathLabel(type, axis)); Gui.SameLine();
    }
    private void DrawComponentExtendedMath(ComponentField field)
    {
        Gui.PushID(field.Name); DrawDocumentedComponentLabel(field);

        var count = field.Type == typeof(Matrix) ? 16 : 4;
        var tableRequired = field.Type == typeof(Matrix) || field.Type == typeof(Rectangle) || field.Type == typeof(RectangleF);
        var matrixTable = tableRequired && Gui.BeginTable("##Coordinates", 4, ImGuiTableFlags.SizingStretchSame);
        for (var axis = 0; axis < count && (!tableRequired || matrixTable); axis++)
        {

            Gui.PushID(axis);

            if (matrixTable) Gui.TableNextColumn();
            if (field.Type != typeof(Matrix)) { Gui.TextUnformatted(ExtendedMathLabel(field.Type, axis)); Gui.SameLine(); }
            Gui.SetNextItemWidth(-1);
            DrawComponentCoordinateDraft(field, axis, "##Value");
            Gui.PopID();
        }
        if (matrixTable) Gui.EndTable();
        if (componentErrors.TryGetValue(field, out var message)) DrawSelectableText("##CoordinateError", message);
        Gui.PopID();
    }
    private static string ExtendedMathLabel(Type type, int axis) => type == typeof(Matrix)
        ? "M" + (axis / 4 + 1).ToString(CultureInfo.InvariantCulture) + (axis % 4 + 1).ToString(CultureInfo.InvariantCulture)
        : type == typeof(Rectangle) || type == typeof(RectangleF) ? "XYWH"[axis].ToString() : "XYZW"[axis].ToString();
}
