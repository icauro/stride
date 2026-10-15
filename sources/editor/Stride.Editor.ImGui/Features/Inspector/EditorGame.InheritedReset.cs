using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawInheritedComponentResetMenu(EntityComponent component)
    {
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, document!.CanResetInheritedComponent(component)))
            Execute(() => { document.ResetInheritedComponent(component); ClearInheritedInspectorState(); });
    }
    private void DrawInheritedEntityResetMenu(Entity entity)
    {
        if (!Gui.BeginPopupContextItem("InheritedEntityActions")) return;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, document!.CanResetInheritedEntity(entity)))
            Execute(() => { document.ResetInheritedEntity(entity); ClearInheritedInspectorState(); });
        Gui.EndPopup();
    }
}
