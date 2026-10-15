using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Native sprite-batch previews pan while any of the five engine mouse buttons is held.</summary>
internal static class EditorPreviewPointerInput
{
    internal static void Draw(string id, Vector2 size)
    {
        var identity = Gui.GetID(id); var context = Gui.GetCurrentContext();
        if (context.ActiveId == identity && context.ActiveIdSource == ImGuiInputSource.Mouse
            && context.ActiveIdMouseButton >= 0 && !Gui.IsMouseDown((ImGuiMouseButton)context.ActiveIdMouseButton))
        {
            // The original service uses HasDownMouseButtons, not the first
            // pressed button alone. Preserve capture until the final release.
            for (var button = 0; button < 5; ++button)
                if (ImGuiP.IsMouseDown((ImGuiMouseButton)button, identity))
                { context.ActiveIdMouseButton = (sbyte)button; Claim(button, identity); break; }
        }
        Gui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft
            | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        if ((ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0
            || !Gui.IsItemHovered() || context.ActiveId != 0) return;
        // Dear ImGui ButtonBehavior polls only the first three buttons for
        // initial activation; its held/release path supports all IO buttons.
        for (var button = 3; button < 5; ++button)
        {
            if (!ImGuiP.IsMouseClicked((ImGuiMouseButton)button, ImGuiInputFlags.None, identity)) continue;
            var window = ImGuiP.GetCurrentWindow();
            ImGuiP.SetActiveID(identity, window);
            context.ActiveIdSource = ImGuiInputSource.Mouse;
            context.ActiveIdMouseButton = (sbyte)button;
            Claim(button, identity);
            ImGuiP.SetFocusID(identity, window); ImGuiP.FocusWindow(window);
            break;
        }
    }
    internal static bool TryPanDelta(out Vector2 delta)
    {
        delta = Vector2.Zero;
        if (!Gui.IsItemActive() || Gui.IsItemActivated()) return false;
        var context = Gui.GetCurrentContext();
        if (context.ActiveIdSource != ImGuiInputSource.Mouse) return false;
        var down = false;
        for (var button = 0; button < 5; ++button)
        {
            // Native HasPressedMouseButtons resets the previous position on
            // every additional press, including while another button is held.
            if (Gui.IsMouseClicked((ImGuiMouseButton)button)) return false;
            down |= Gui.IsMouseDown((ImGuiMouseButton)button);
        }
        if (!down) return false;
        delta = Gui.GetIO().MouseDelta;
        return delta != Vector2.Zero;
    }
    private static void Claim(int button, uint identity)
        => ImGuiP.SetKeyOwner(ImGuiP.MouseButtonToKey((ImGuiMouseButton)button), identity);
}
