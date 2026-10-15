using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawNullableComponentPresence(ComponentField field)
    {
        Gui.PushID(field.Name + "/presence");
        var present = field.Node.Retrieve() != null;
        Gui.BeginDisabled(!document!.CanEditComponentValue(field) || !field.SupportsEditing);
        if (DrawNativeNullablePresence(present, out var create, field.AllowsNullObject))
        {
            try
            {
                if (ReferenceEquals(activeColorPopup, field)) activeColorPopup = null;
                document.EndComponentEdit();
                object? initial = create ? CreateNullableValue(field.Type) : null;
                document.UpdateComponent(field, initial); document.EndComponentEdit(); componentErrors.Remove(field);
            }
            catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
        }
        Gui.EndDisabled();
        present = field.Node.Retrieve() != null;
        Gui.PopID(); return present;
    }
}
