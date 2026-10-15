using Hexa.NET.ImGui;
using Stride.Assets.Scripts;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawVisualScriptCanvas(VisualScriptDocument document, Method method, VisualScriptCanvasState state)
    {
        bool Editable() => CanMutateDocumentPresentation(document) && document.CanSave;
        if (!Editable() || state.GestureRevision >= 0 && state.GestureRevision != document.EditRevision)
        { state.Connecting = null; state.DragStart.Clear(); state.DragOffset = Vector2.Zero; state.GestureRevision = -1; }
        var origin = Gui.GetCursorScreenPos(); var size = Vector2.Max(Gui.GetContentRegionAvail(), Vector2.One);
        var end = origin + size; var draw = Gui.GetWindowDrawList(); var io = Gui.GetIO(); var mouse = io.MousePos;
        RegisterVisualCaptureRegion("visual-scripts.graph", "section", "Visual script graph", origin, end, "visual-scripts.editor");
        if (state.LayoutRevision != document.EditRevision || state.LayoutFontSize != Gui.GetFontSize())
        {
            state.LayoutRevision = document.EditRevision; state.LayoutFontSize = Gui.GetFontSize();
            state.Blocks = method.Blocks.Values.ToArray(); state.Links = method.Links.Values.ToArray();
            state.Layout.Clear(); state.SlotOffsets.Clear();
            foreach (var block in state.Blocks)
            {
                var title = block.Title ?? block.GetType().Name;
                var inputs = block.Slots.Count(slot => slot.Direction == SlotDirection.Input);
                var outputs = block.Slots.Count - inputs;
                var labelWidth = block.Slots.Count == 0 ? 0 : block.Slots.Max(slot => Gui.CalcTextSize(slot.Name ?? "").X);
                var blockSize = new Vector2(Math.Max(220, Math.Max(Gui.CalcTextSize(title).X + 35, labelWidth * 2 + 50)), 36 + Math.Max(1, Math.Max(inputs, outputs)) * 25);
                state.Layout[block] = (blockSize, title); inputs = outputs = 0;
                foreach (var slot in block.Slots)
                    state.SlotOffsets[slot] = new(slot.Direction == SlotDirection.Input ? 0 : blockSize.X,
                        45 + (slot.Direction == SlotDirection.Input ? inputs++ : outputs++) * 25);
            }
        }
        Gui.InvisibleButton("##Canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = Gui.IsItemHovered();
        if (hovered && io.MouseWheel != 0)
        {
            var point = (mouse - origin - state.Pan) / state.Zoom;
            state.Zoom = Math.Clamp(state.Zoom * MathF.Pow(1.12f, io.MouseWheel), .2f, 3f);
            state.Pan = mouse - origin - point * state.Zoom;
        }
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Middle)) state.Panning = true;
        if (!Gui.IsMouseDown(ImGuiMouseButton.Middle)) state.Panning = false;
        if (state.Panning) state.Pan += io.MouseDelta;
        Vector2 Screen(Vector2 point) => origin + state.Pan + point * state.Zoom;
        Vector2 Position(Block block)
        {
            var position = state.DragStart.TryGetValue(block.Id, out var saved) ? saved : block.Position;
            return new Vector2(position.X, position.Y) + (state.DragStart.ContainsKey(block.Id) ? state.DragOffset : Vector2.Zero);
        }
        Vector2 BlockSize(Block block) => state.Layout[block].Size;
        Vector2 SlotPosition(Slot slot)
        {
            return Screen(Position(slot.Owner!) + state.SlotOffsets[slot]);
        }
        bool ValidSlot(Slot? slot) => slot?.Owner != null && state.Layout.ContainsKey(slot.Owner) && state.SlotOffsets.ContainsKey(slot);
        draw.PushClipRect(origin, end, true); draw.AddRectFilled(origin, end, 0xFF242424);
        var grid = 16 * state.Zoom;
        if (grid >= 8)
        {
            for (var x = state.Pan.X % grid; x < size.X; x += grid) draw.AddLine(origin + new Vector2(x, 0), origin + new Vector2(x, size.Y), 0xFF303030);
            for (var y = state.Pan.Y % grid; y < size.Y; y += grid) draw.AddLine(origin + new Vector2(0, y), origin + new Vector2(size.X, y), 0xFF303030);
        }
        Link? hitLink = null;
        foreach (var link in state.Links)
        {
            if (!ValidSlot(link.Source) || !ValidSlot(link.Target)) continue;
            var from = SlotPosition(link.Source); var to = SlotPosition(link.Target); var handle = Math.Max(50 * state.Zoom, Math.Abs(to.X - from.X) * .5f);
            var p1 = from + new Vector2(handle, 0); var p2 = to - new Vector2(handle, 0);
            var color = state.SelectedLinks.Contains(link.Id) ? 0xFF50C5FFu : link.Source.Kind == SlotKind.Execution ? 0xFFE5E5E5u : 0xFFF0B97Eu;
            draw.AddBezierCubic(from, p1, p2, to, color, Math.Max(1, 2 * state.Zoom), 0);
            var previous = from;
            for (var segment = 1; segment <= 24; segment++)
            {
                var t = segment / 24f; var s = 1 - t;
                var point = s * s * s * from + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * to;
                var delta = point - previous; var u = delta.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(mouse - previous, delta) / delta.LengthSquared(), 0, 1);
                if (Vector2.DistanceSquared(mouse, previous + u * delta) < 36) hitLink = link;
                previous = point;
            }
        }
        Block? hitBlock = null; Slot? hitSlot = null;
        foreach (var block in state.Blocks)
        {
            var top = Screen(Position(block)); var bottom = top + BlockSize(block) * state.Zoom;
            RegisterVisualCaptureRegion("visual-scripts.graph.block." + block.Id, "control", state.Layout[block].Title, top, bottom, "visual-scripts.graph");
            if (mouse.X >= top.X && mouse.X <= bottom.X && mouse.Y >= top.Y && mouse.Y <= bottom.Y) hitBlock = block;
            draw.AddRectFilled(top, bottom, 0xFF3A3A3A, 5);
            draw.AddRectFilled(top, new Vector2(bottom.X, top.Y + 31 * state.Zoom), 0xFF555555, 5);
            draw.AddRect(top, bottom, state.SelectedBlocks.Contains(block.Id) ? 0xFF50C5FFu : 0xFF777777u, 5, ImDrawFlags.None, state.SelectedBlocks.Contains(block.Id) ? 2 : 1);
            draw.AddText(top + new Vector2(10, 7) * state.Zoom, 0xFFFFFFFF, state.Layout[block].Title);
            foreach (var slot in block.Slots)
            {
                var position = SlotPosition(slot); var color = slot.Kind == SlotKind.Execution ? 0xFFFFFFFFu : 0xFFF0B97Eu;
                var radius = new Vector2(5 * state.Zoom);
                RegisterVisualCaptureRegion("visual-scripts.graph.slot." + slot.Id, "control", slot.Name ?? "", position - radius, position + radius, "visual-scripts.graph.block." + block.Id);
                draw.AddCircleFilled(position, 5 * state.Zoom, color);
                var label = slot.Name ?? ""; var textSize = Gui.CalcTextSize(label);
                draw.AddText(position + new Vector2(slot.Direction == SlotDirection.Input ? 10 : -textSize.X - 10, -8), color, label);
                if (Vector2.DistanceSquared(mouse, position) <= MathF.Pow(Math.Max(8, 7 * state.Zoom), 2)) hitSlot = slot;
            }
        }
        if (state.Connecting is { } connecting)
        {
            if (!ValidSlot(connecting)) state.Connecting = null;
            else
            {
                var start = SlotPosition(connecting); var direction = connecting.Direction == SlotDirection.Output ? 1 : -1;
                draw.AddBezierCubic(start, start + new Vector2(70 * direction, 0), mouse - new Vector2(70 * direction, 0), mouse, 0xFF50C5FF, 2, 0);
            }
        }
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hitSlot != null && Editable()) TryVisualScriptPresentationCommand(document, method, () =>
            { state.Connecting = hitSlot; state.GestureRevision = document.EditRevision; });
            else if (hitBlock != null)
            {
                if (!io.KeyCtrl && !state.SelectedBlocks.Contains(hitBlock.Id)) { state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); }
                if (io.KeyCtrl && state.SelectedBlocks.Contains(hitBlock.Id)) state.SelectedBlocks.Remove(hitBlock.Id);
                else state.SelectedBlocks.Add(hitBlock.Id);
                if (Editable()) TryVisualScriptPresentationCommand(document, method, () =>
                {
                    state.GestureRevision = document.EditRevision;
                    foreach (var block in method.Blocks.Values.Where(block => state.SelectedBlocks.Contains(block.Id))) state.DragStart[block.Id] = block.Position;
                });
            }
            else if (hitLink != null)
            {
                if (!io.KeyCtrl) { state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); }
                if (!state.SelectedLinks.Add(hitLink.Id) && io.KeyCtrl) state.SelectedLinks.Remove(hitLink.Id);
            }
            else { if (!io.KeyCtrl) { state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); } state.Marquee = (mouse - origin - state.Pan) / state.Zoom; }
        }
        if (state.DragStart.Count != 0 && Gui.IsMouseDown(ImGuiMouseButton.Left)) state.DragOffset += io.MouseDelta / state.Zoom;
        if (state.Marquee is { } marquee)
        {
            var from = Screen(marquee); var low = Vector2.Min(from, mouse); var high = Vector2.Max(from, mouse);
            draw.AddRectFilled(low, high, 0x3050C5FF); draw.AddRect(low, high, 0xFF50C5FF);
            if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                foreach (var block in method.Blocks.Values)
                {
                    var top = Screen(Position(block)); var bottom = top + BlockSize(block) * state.Zoom;
                    if (top.X <= high.X && bottom.X >= low.X && top.Y <= high.Y && bottom.Y >= low.Y) state.SelectedBlocks.Add(block.Id);
                }
                state.Marquee = null;
            }
        }
        if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (state.Connecting is { } first && hitSlot is { } second && hovered && Editable() && document.CanConnect(method, first, second))
                TryVisualScriptPresentationCommand(document, method, () => document.Connect(method, first, second));
            state.Connecting = null;
            if (state.DragStart.Count != 0)
            {
                var positions = state.DragStart.ToDictionary(pair => pair.Key, pair => new Int2(
                    (int)MathF.Round((pair.Value.X + state.DragOffset.X) / 16) * 16, (int)MathF.Round((pair.Value.Y + state.DragOffset.Y) / 16) * 16));
                if (Editable() && state.DragOffset.LengthSquared() > 0) TryVisualScriptPresentationCommand(document, method, () => document.MoveBlocks(method, positions));
                state.DragStart.Clear(); state.DragOffset = Vector2.Zero;
            }
            state.GestureRevision = -1;
        }
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            state.PopupPosition = (mouse - origin - state.Pan) / state.Zoom; state.ContextSlot = hitSlot; Gui.OpenPopup("Graph menu");
        }
        draw.PopClipRect();
        if (Gui.BeginPopup("Graph menu"))
        {
            if (state.ContextSlot is { } slot && ValidSlot(slot) && DrawNativeMenuItem("Disconnect", "", false, Editable())) TryVisualScriptPresentationCommand(document, method, () => document.Disconnect(method, slot));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Del", false, Editable() && (state.SelectedBlocks.Count != 0 || state.SelectedLinks.Count != 0))) TryVisualScriptPresentationCommand(document, method, () => document.DeleteGraphSelection(method, state.SelectedBlocks, state.SelectedLinks));
            Gui.Separator(); visualScriptTemplates = GetVisualScriptBlockTemplates();
            DrawVisualScriptMethodSearch(document, method, state);
            foreach (var group in visualScriptTemplates.GroupBy(template => template.Category))
                if (BeginNativeMenu(group.Key, Editable()))
                {
                    foreach (var template in group)
                        if (DrawNativeMenuItem(template.Name)) TryVisualScriptPresentationCommand(document, method, () => document.AddBlock(method, template.Create(), new(
                            (int)MathF.Round(state.PopupPosition.X / 16) * 16, (int)MathF.Round(state.PopupPosition.Y / 16) * 16), GetVisualScriptCompilation(document)));
                    Gui.EndMenu();
                }
            Gui.EndPopup();
        }
        if (Gui.IsWindowFocused() && !io.WantTextInput)
        {
            if (Gui.IsKeyPressed(ImGuiKey.Escape, false)) { state.Connecting = null; state.Marquee = null; state.DragStart.Clear(); state.DragOffset = Vector2.Zero; }
            if (Editable() && Gui.IsKeyPressed(ImGuiKey.Delete, false)) TryVisualScriptPresentationCommand(document, method, () => document.DeleteGraphSelection(method, state.SelectedBlocks, state.SelectedLinks));
            if (io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.A, false)) foreach (var block in method.Blocks.Values) state.SelectedBlocks.Add(block.Id);
        }
    }
}
