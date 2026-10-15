using Stride.Core;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ScenePickingOptions scenePickingOptions = new();
    private void UpdatePickingOptions() => engineViewport?.SetPickingTypeFilter(scenePickingOptions.Predicate, scenePickingOptions.Version);
    private void DrawPickingTypeControls(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var options = context?.Picking ?? scenePickingOptions;
        if (!Gui.BeginMenu(EditorLocalization.Widget("Selection"))) return;
        foreach (var type in EntityHierarchyDocument.RegisteredComponentTypes.OrderBy(type => DisplayAttribute.GetDisplayName(type), StringComparer.OrdinalIgnoreCase))
        {
            Gui.PushID(type.FullName + "," + type.Assembly.GetName().Name);
            var enabled = !options.IsExcluded(type);
            if (Gui.Checkbox(DisplayAttribute.GetDisplayName(type), ref enabled))
            {
                options.SetExcluded(type, !enabled);
                if (context == null) UpdatePickingOptions();
                else context.Viewport.SetPickingTypeFilter(options.Predicate, options.Version);
            }
            Gui.PopID();
        }
        Gui.EndMenu();
    }
}
