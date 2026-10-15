using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentDefaultAction(ComponentField field)
    {
        var popupId = field.Name + "/default-actions";
        using var nativeTarget = field.Presentation?.AcquireCommandTarget(field);
        if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) && !Gui.IsPopupOpen(popupId)) return;
        if (!Gui.BeginPopupContextItem(popupId)) return;
        if (DrawNativeMenuItem("Reset to declared default", "", false, field.DefaultValue != null && document!.CanEditComponentValue(field)))
        {
            try
            {
                var value = field.DefaultValue!.Value;
                if (value != null && !field.Type.IsInstanceOfType(value))
                    value = field.Type.IsEnum ? value is string name ? Enum.Parse(field.Type, name) : Enum.ToObject(field.Type, value)
                        : Convert.ChangeType(value, field.Type, CultureInfo.InvariantCulture);
                document!.EndComponentEdit();
                document.UpdateComponent(field, value); document.EndComponentEdit();
                activeColorPopup = null; componentErrors.Remove(field);
            }
            catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
        }
        DrawPropertyClipboardCommands(document!, field);
        DrawRegisteredPropertyCommands(document!, field.Node, document!.CanEditExtensionProperty(field), document.ApplyInheritedGraphMutation);
        Gui.EndPopup();
    }
}
