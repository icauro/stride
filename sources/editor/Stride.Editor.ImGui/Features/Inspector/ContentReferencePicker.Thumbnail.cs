using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private void DrawReferenceThumbnail(Type type, object? value, AssetEntry? entry, float size, bool allowsNull,
        bool canEdit, bool mixed, bool forceAssignment, bool highlighted, Action<object?> commit)
    {
        var position = Gui.GetCursorScreenPos();
        var fetch = !mixed && host.CanFetch(value);
        var empty = mixed || value == null;
        using (ImGuiScopes.Disabled(!fetch && (!empty || !canEdit)))
        {
            if (Gui.InvisibleButton("##ReferenceThumbnail", new Vector2(size)))
            {
                if (fetch) host.Fetch(value);
                else if (empty && canEdit) RequestModal(type, value, commit, mixed, forceAssignment);
            }
        }
        var hovered = Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (canEdit) DrawDrop(type, commit, true);
        if (hovered)
        {
            Gui.SetMouseCursor(ImGuiMouseCursor.Hand);
            Gui.SetTooltip(EditorLocalization.Text(empty ? "Select an asset" : "Select the referenced asset", "ToolTip"));
            if (Gui.IsMouseReleased(ImGuiMouseButton.Right)) Gui.OpenPopup("##ReferenceThumbnailActions");
        }
        var draw = Gui.GetWindowDrawList();
        if (Gui.IsRectVisible(position, position + new Vector2(size)))
        {
            if (entry != null) host.RenderThumbnail(entry, position, size);
            else if (empty && host.PickerIcons is { } icons)
            {
                var iconSize = 16 * Gui.GetStyle().FontScaleDpi;
                var minimum = position + new Vector2((size - iconSize) * .5f);
                draw.AddImage(icons.Get("open"), minimum, minimum + new Vector2(iconSize));
            }
            if (highlighted) draw.AddRectFilled(position, position + new Vector2(size),
                Gui.GetColorU32(new Vector4(1, 207 / 255f, 0, .5f)));
        }
        if (Gui.BeginPopup("##ReferenceThumbnailActions"))
        {
            try
            {
                DrawReferenceActions(type, value, allowsNull, canEdit, mixed, forceAssignment, commit);
            }
            finally { Gui.EndPopup(); }
        }
    }
}
