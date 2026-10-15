using Stride.Core.Yaml;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawUnloadableProperty(IUnloadable value)
    {
        var diagnostic = InspectorUnloadableDiagnostic.Read(value);
        DrawSelectableText("##UnloadedType", diagnostic.Heading);
        DrawSelectableText("##UnloadError", diagnostic.Error);
    }
    private bool DrawUnloadableProperties(IReadOnlyList<object?> values)
    {
        var found = false;
        for (var index = 0; index < values.Count; index++)
            if (values[index] is IUnloadable unknown)
            {
                found = true; Gui.PushID(index);
                try { DrawUnloadableProperty(unknown); } finally { Gui.PopID(); }
            }
        return found;
    }
}
