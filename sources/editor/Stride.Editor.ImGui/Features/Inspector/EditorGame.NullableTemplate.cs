using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawNativeNullablePresence(bool present, out bool create, bool allowsNull = true)
    {
        create = !present;
        using var disabled = ImGuiScopes.Disabled(present && !allowsNull);
        var tooltip = EditorLocalization.Text(present ? "Clear value (set to null)" : "Create an instance of this structure", "ToolTip");
        var clicked = icons != null ? icons.Button(present ? "property-clear" : "property-create", tooltip, 16)
            : Gui.SmallButton(EditorLocalization.Text(present ? "Clear" : "Create"));
        return clicked && Services.Commands.TryExecute(ValidatePendingPropertyStrings);
    }

    private static object? CreateNullableValue(Type type) => ObjectFactoryRegistry.CanCreateInstance(type)
        ? ObjectFactoryRegistry.NewInstance(type) : null;
}
