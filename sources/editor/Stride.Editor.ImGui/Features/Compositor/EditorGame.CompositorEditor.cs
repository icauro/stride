using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Assets.Rendering;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using Vector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly ConditionalWeakTable<AssetMetadataDocument, CompositorEditorState> compositorEditors = new();
    private void DrawCompositorEditor(AssetMetadataDocument document)
    {
        if (document.CompositorAsset == null) return;
        var state = compositorEditors.GetValue(document, static _ => new CompositorEditorState());
        if (state.Revision != document.EditRevision)
        {
            state.Revision = document.EditRevision; ReconcileCompositorLinkSelection(document, state); ReconcileCompositorConnection(document, state);
            state.ListRows.Clear();
            if (state.EditingListItem is { } editing && !document.CompositorItems.Any(item => item.ItemId == editing && item.Collection == state.EditingListCollection)) state.EditingListItem = null;
            state.SelectedBlocks.IntersectWith(document.CompositorBlocks.Select(block => block.Id));
            state.SelectedItems.IntersectWith(document.CompositorItems.Select(item => item.ItemId));
        }
        var version = CompositorTypeCatalog.Version;
        if (state.AssemblyVersion != version)
        {
            state.RendererTypes = AssetMetadataDocument.CompositorRendererTypes(); state.FeatureTypes = AssetMetadataDocument.CompositorFeatureTypes(); state.AssemblyVersion = version;
        }
        DrawCompositorSplitLayout(document, state);
    }
    private void DrawCompositorList(AssetMetadataDocument document, CompositorEditorState state, string collection, string title)
    {
        var rows = CompositorListRows(document, state, collection);
        Gui.PushID(collection); Gui.TextUnformatted(title); Gui.SameLine();
        Action? action = null;
        Gui.BeginDisabled(!document.CanAddCompositorItem(collection));
        if (Gui.SmallButton("+"))
        {
            if (collection == nameof(GraphicsCompositorAsset.RenderFeatures)) Gui.OpenPopup("Create feature");
            else action = () => document.AddCompositorItem(collection);
        }
        Gui.EndDisabled();
        Gui.SameLine();
        var selected = rows.Where(item => state.SelectedItems.Contains(item.ItemId)).ToArray();
        Gui.BeginDisabled(selected.Length == 0 || selected.Any(item => !document.CanRemoveCompositorItem(item)));
        if (Gui.SmallButton("−")) action = CaptureCompositorListRemoval(document, state, collection, selected);
        Gui.EndDisabled();
        if (Gui.BeginPopup("Create feature"))
        {
            foreach (var type in state.FeatureTypes)
                if (DrawNativeMenuItem(DisplayAttribute.GetDisplayName(type))) { var chosen = type; action = () => document.AddCompositorItem(collection, chosen); }
            Gui.EndPopup();
        }
        if (Gui.BeginChild("Items", new NVector2(0, Math.Max(1, Gui.GetContentRegionAvail().Y)), ImGuiChildFlags.Borders))
        {
            HandleCompositorListKeyboard(document, state, collection, rows);
            foreach (var item in rows)
            {
                Gui.PushID(item.ItemId.ToString());
                if (DrawCompositorListNameEdit(document, state, item)) { Gui.PopID(); continue; }
                var labelPosition = Gui.GetCursorScreenPos();
                PrepareCompositorKeyboardRow(state, item);
                if (Gui.Selectable("##Item", state.SelectedItems.Contains(item.ItemId)))
                    SelectCompositorListItemAfterPending(document, state, item, Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift);
                ObserveCompositorKeyboardRow(document, state, item);
                if (document.CanRenameCompositorListItem(item) && Gui.IsItemHovered() && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    BeginCompositorListNameEdit(document, state, item);
                DrawCompositorListItemName(item, labelPosition);
                DrawCompositorItemReorder(document, state, item, moved => action = moved);
                Gui.PopID();
            }
        }
        ImGuiScopes.EndChild();
        if (action != null) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, action); }
        Gui.PopID();
    }
    private unsafe void DrawCompositorCanvas(AssetMetadataDocument document, CompositorEditorState state)
    {
        var origin = Gui.GetCursorScreenPos(); var size = Gui.GetContentRegionAvail();
        size = NVector2.Max(size, NVector2.One);
        Gui.InvisibleButton("Canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        Controls.Record("compositor/canvas");
        var hovered = Gui.IsItemHovered(); var io = Gui.GetIO(); var mouse = io.MousePos;
        if (io.AppFocusLost || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            || !Gui.IsMouseDown(ImGuiMouseButton.Left) && !Gui.IsMouseReleased(ImGuiMouseButton.Left))
        { state.Linking = null; state.LinkingCertificate = null; }
        if (hovered && Gui.IsMouseDragging(ImGuiMouseButton.Middle)) state.Pan += io.MouseDelta;
        if (hovered && io.MouseWheel != 0)
        {
            var previous = state.Zoom; state.Zoom = Math.Clamp(previous * MathF.Pow(1.15f, io.MouseWheel), 0.2f, 2.5f);
            state.Pan = mouse - origin - (mouse - origin - state.Pan) * (state.Zoom / previous);
        }
        var draw = Gui.GetWindowDrawList(); draw.PushClipRect(origin, origin + size, true);
        var scale = state.Zoom * UiScale(1);
        var grid = 32 * scale;
        for (var x = state.Pan.X % grid; x < size.X; x += grid) draw.AddLine(origin + new NVector2(x, 0), origin + new NVector2(x, size.Y), 0x203F3F3Fu);
        for (var y = state.Pan.Y % grid; y < size.Y; y += grid) draw.AddLine(origin + new NVector2(0, y), origin + new NVector2(size.X, y), 0x203F3F3Fu);
        state.BoundsMin.Clear(); state.BoundsMax.Clear(); state.Pins.Clear(); state.Inputs.Clear();
        var blocks = document.CompositorBlocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i]; var fallback = FallbackCompositorPosition(i);
            var actual = document.CompositorBlockPosition(block.Id, new Vector2(fallback.X, fallback.Y));
            var min = origin + state.Pan + new NVector2(actual.X, actual.Y) * scale;
            var max = min + new NVector2(260, Math.Max(64, 40 + block.Slots.Count * 24)) * scale;
            state.BoundsMin[block.Id] = min; state.BoundsMax[block.Id] = max;
            if (block.Id != Guid.Empty) state.Inputs[block.Id] = min + new NVector2(0, 28 * scale);
            for (var pin = 0; pin < block.Slots.Count; pin++) state.Pins[block.Slots[pin]] = new(max.X, min.Y + (44 + pin * 24) * scale);
        }
        CompositorSlot? clickedLink = null;
        foreach (var block in blocks)
            foreach (var slot in block.Slots)
                if (slot.Renderer is { } renderer && state.Inputs.TryGetValue(renderer.Id, out var end))
                {
                    var start = state.Pins[slot]; var distance = Math.Max(50 * scale, Math.Abs(end.X - start.X) * 0.5f);
                    draw.AddBezierCubic(start, start + new NVector2(distance, 0), end - new NVector2(distance, 0), end,
                        state.SelectedLinks.Contains(slot) ? 0xFF66CCFFu : 0xFF999999u, 2 * UiScale(1));
                    if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && NearBezier(mouse, start, start + new NVector2(distance, 0), end - new NVector2(distance, 0), end, 6 * UiScale(1))) clickedLink = slot;
                }
        Guid? clickedBlock = null; CompositorSlot? clickedPin = null; Guid? releasedInput = null;
        foreach (var block in blocks)
        {
            var min = state.BoundsMin[block.Id]; var max = state.BoundsMax[block.Id];
            draw.AddRectFilled(min, max, state.SelectedBlocks.Contains(block.Id) ? 0xFF484848u : 0xFF303030u, 5 * scale);
            draw.AddRect(min, max, state.SelectedBlocks.Contains(block.Id) ? 0xFF66CCFFu : 0xFF777777u, 5 * scale);
            draw.AddText(Gui.GetFont().Handle, Gui.GetFontSize() * state.Zoom, min + new NVector2(12, 10) * scale, Gui.GetColorU32(ImGuiCol.Text), block.Title);
            RecordCompositorWorkflowBlock(block.Id, min + new NVector2(60, 16) * scale, origin, origin + size);
            if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && Inside(mouse, min, max)) clickedBlock = block.Id;
            if (state.Inputs.TryGetValue(block.Id, out var input))
            {
                draw.AddCircleFilled(input, 6 * scale, 0xFFCCAA66u);
                if (hovered && Gui.IsMouseReleased(ImGuiMouseButton.Left) && NVector2.DistanceSquared(mouse, input) <= 144 * scale * scale) releasedInput = block.Id;
            }
            foreach (var slot in block.Slots)
            {
                var pin = state.Pins[slot]; draw.AddText(Gui.GetFont().Handle, Gui.GetFontSize() * state.Zoom, new NVector2(min.X + 12 * scale, pin.Y - 8 * scale), Gui.GetColorU32(ImGuiCol.Text), slot.Name);
                if (slot.Renderer == null) draw.AddCircle(pin, 6 * scale, 0xFFCCAA66u, 0, 2 * scale); else draw.AddCircleFilled(pin, 6 * scale, 0xFFCCAA66u);
                if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && NVector2.DistanceSquared(mouse, pin) <= 144 * scale * scale) clickedPin = slot;
            }
        }
        Action? command = null;
        if (clickedPin != null && document.CanEditCompositorSlot(clickedPin))
        { BeginCompositorConnection(document, state, clickedPin); clickedBlock = null; clickedLink = null; }
        else if (clickedBlock is { } id)
        {
            var pressed = blocks.First(block => block.Id == id);
            var selected = SelectCompositorBlocksAfterPending(document, state, [pressed], false, false, false);
            if (selected)
            {
                state.PressedBlock = pressed;
                state.PressedGraphPosition = (mouse - origin - state.Pan) / scale;
            }
            if (selected && state.SelectedBlocks.Contains(id) && state.SelectedBlocks.All(document.CanMoveCompositorBlock))
            {
                state.Dragging = id; state.DragStart = mouse; state.DragStarts.Clear();
                foreach (var selectedBlock in state.SelectedBlocks)
                {
                    var index = blocks.ToList().FindIndex(block => block.Id == selectedBlock); var fallback = FallbackCompositorPosition(index);
                    var position = document.CompositorBlockPosition(selectedBlock, new Vector2(fallback.X, fallback.Y)); state.DragStarts[selectedBlock] = new(position.X, position.Y);
                }
            }
        }
        else if (clickedLink != null)
        {
            SelectCompositorLinkAfterPending(document, state, clickedLink);
        }
        else if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && state.Linking == null)
        { state.SelectionStart = mouse; }
        if (state.SelectionStart is { } selectionStart)
        {
            var min = NVector2.Min(selectionStart, mouse); var max = NVector2.Max(selectionStart, mouse);
            if (Gui.IsMouseDragging(ImGuiMouseButton.Left))
            { draw.AddRectFilled(min, max, 0x3066CCFFu); draw.AddRect(min, max, 0xFF66CCFFu); }
            if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                state.SelectionStart = null;
                // Native GraphSelectionBehavior treats either zero extent as a point,
                // selecting the first containing vertex rather than intersecting a line.
                var pointSelection = min.X == max.X || min.Y == max.Y;
                var candidates = blocks.Where(block => state.BoundsMin[block.Id].X <= (pointSelection ? min.X : max.X)
                    && state.BoundsMax[block.Id].X >= min.X
                    && state.BoundsMin[block.Id].Y <= (pointSelection ? min.Y : max.Y)
                    && state.BoundsMax[block.Id].Y >= min.Y);
                var hits = (pointSelection ? candidates.Take(1) : candidates).ToArray();
                SelectCompositorBlocksAfterPending(document, state, hits, io.KeyCtrl, io.KeyShift, true);
            }
        }
        if (state.Linking is { } linking)
        {
            if (state.Pins.TryGetValue(linking, out var pin)) draw.AddBezierCubic(pin, pin + new NVector2(80 * scale, 0), mouse - new NVector2(80 * scale, 0), mouse, 0xFF66CCFFu, 2 * scale);
            if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                if (releasedInput is { } target) command = CaptureCompositorConnection(document, state, target);
                state.Linking = null; state.LinkingCertificate = null;
            }
        }
        if (state.Dragging != null)
        {
            if (Gui.IsMouseDragging(ImGuiMouseButton.Left))
                foreach (var item in state.DragStarts)
                { var position = item.Value + (mouse - state.DragStart) / scale; document.StageCompositorBlockPosition(item.Key, new Vector2(position.X, position.Y)); }
            if (Gui.IsMouseReleased(ImGuiMouseButton.Left)) { state.Dragging = null; command = document.EndCompositorSettingEdit; }
        }
        if (io.AppFocusLost || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) state.PressedBlock = null;
        if (state.PressedBlock is { } releasedBlock && Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            state.PressedBlock = null;
            // Native graph selection compares graph-space press/release positions exactly:
            // an unmoved click collapses the group, while group dragging keeps its scope.
            if (state.PressedGraphPosition == (mouse - origin - state.Pan) / scale)
                SelectCompositorBlocksAfterPending(document, state, [releasedBlock], false, false, true);
        }
        if (hovered && Gui.IsMouseReleased(ImGuiMouseButton.Right)) { state.PopupPosition = (mouse - origin - state.Pan) / scale; Gui.OpenPopup("Compositor actions"); }
        if (Gui.BeginPopup("Compositor actions"))
        {
            Gui.BeginDisabled(!document.CanEditCompositor);
            if (BeginNativeMenu("Create"))
            {
                foreach (var type in state.RendererTypes)
                    if (DrawNativeMenuItem(DisplayAttribute.GetDisplayName(type))) { var chosen = type; command = () => document.AddCompositorItem(nameof(GraphicsCompositorAsset.SharedRenderers), chosen, new Vector2(state.PopupPosition.X, state.PopupPosition.Y)); }
                Gui.EndMenu();
            }
            if (DrawNativeMenuItem(EditorLocalization.Widget("Delete selection", "Menu"), "Delete", false,
                state.SelectedBlocks.Any(id => id != Guid.Empty) || state.SelectedLinks.Count > 0 || state.SelectedItems.Count > 0))
                command = CaptureCompositorDeleteSelection(document, state);
            Gui.EndDisabled(); Gui.EndPopup();
        }
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !io.WantTextInput && Gui.IsKeyPressed(ImGuiKey.Delete))
        {
            command = CaptureCompositorDeleteSelection(document, state);
        }
        draw.PopClipRect();
        if (command != null) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, command); }
    }
    private unsafe void DrawCompositorItemReorder(AssetMetadataDocument document, CompositorEditorState state, CompositorListItem item, Action<Action> defer)
    {
        if (document.CanRemoveCompositorItem(item) && Gui.BeginDragDropSource())
        {
            if (state.ListDrag?.ItemId != item.ItemId) { state.ListDrag = item; state.ItemDragToken = Guid.NewGuid(); }
            var token = state.ItemDragToken; Gui.SetDragDropPayload("STRIDE_COMPOSITOR_LIST", &token, (nuint)sizeof(Guid));
            Gui.TextUnformatted(item.Name); Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload("STRIDE_COMPOSITOR_LIST");
        if (!payload.IsNull && payload.DataSize == sizeof(Guid) && payload.IsDelivery() && *(Guid*)payload.Data == state.ItemDragToken
            && state.ListDrag is { } source && source.Collection == item.Collection && source.ItemId != item.ItemId)
        {
            var rows = document.CompositorItems.Where(row => row.Collection == item.Collection).ToArray();
            var target = Array.FindIndex(rows, row => row.ItemId == item.ItemId);
            var after = Gui.GetIO().MousePos.Y >= (Gui.GetItemRectMin().Y + Gui.GetItemRectMax().Y) * 0.5f;
            var original = Array.FindIndex(rows, row => row.ItemId == source.ItemId);
            var destination = Math.Clamp(target + (after ? 1 : 0) - (original < target + (after ? 1 : 0) ? 1 : 0), 0, rows.Length - 1);
            defer(() => document.MoveCompositorListItem(source, destination)); state.ListDrag = null;
        }
        Gui.EndDragDropTarget();
    }
    private static NVector2 FallbackCompositorPosition(int index) => index == 0 ? NVector2.Zero : new NVector2(340 + ((index - 1) / 4) * 340, ((index - 1) % 4) * 170);
    private static bool Inside(NVector2 point, NVector2 min, NVector2 max) => point.X >= min.X && point.Y >= min.Y && point.X <= max.X && point.Y <= max.Y;
    private static bool NearBezier(NVector2 point, NVector2 a, NVector2 b, NVector2 c, NVector2 d, float radius)
    {
        var previous = a;
        for (var i = 1; i <= 24; i++)
        {
            var t = i / 24f; var s = 1 - t; var next = s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d;
            var delta = next - previous; var denominator = delta.LengthSquared();
            var nearest = previous + delta * (denominator == 0 ? 0 : Math.Clamp(NVector2.Dot(point - previous, delta) / denominator, 0, 1));
            if (NVector2.DistanceSquared(point, nearest) <= radius * radius) return true;
            previous = next;
        }
        return false;
    }
}
