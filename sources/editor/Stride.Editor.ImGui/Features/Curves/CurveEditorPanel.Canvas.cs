using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class CurveEditorPanel
{
    private void DrawCanvas(ICurveTrack track)
    {
        var origin = Gui.GetCursorScreenPos(); var size = Gui.GetContentRegionAvail();
        var scale = Gui.GetStyle().FontScaleDpi;
        if (size.X < 80 * scale || size.Y < 80 * scale) return;
        var minimum = origin + new NVector2(48, 10) * scale; var maximum = origin + size - new NVector2(8, 25) * scale; var extent = maximum - minimum;
        Gui.InvisibleButton("##CurveCanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        var hovered = Gui.IsItemHovered(); var io = Gui.GetIO(); var mouse = io.MousePos;
        var uniformAxes = !io.KeyCtrl && !io.KeyShift && !io.KeyAlt && !io.KeySuper;
        var manipulateX = uniformAxes || io.KeyCtrl;
        var manipulateY = uniformAxes || io.KeyShift;
        NVector2 Plot(float key, float value) => minimum + new NVector2((Axis(key, logX) - xMin) / (xMax - xMin) * extent.X, (yMax - Axis(value, logY)) / (yMax - yMin) * extent.Y);
        NVector2 Unplot(NVector2 screen) => new(Unaxis(xMin + (screen.X - minimum.X) / extent.X * (xMax - xMin), logX), Unaxis(yMax - (screen.Y - minimum.Y) / extent.Y * (yMax - yMin), logY));
        var points = track.ReadPoints().ToArray();
        if (Gui.IsKeyPressed(ImGuiKey.Escape) || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) boxStart = null;
        if (selected.Count != 0)
        {
            try
            {
                foreach (var point in points) pointMembership.Add(point.Value);
                selected.RemoveWhere(value => !pointMembership.Contains(value));
            }
            finally { pointMembership.Clear(); }
        }
        if (hovered && Gui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var dx = io.MouseDelta.X / extent.X * (xMax - xMin); var dy = io.MouseDelta.Y / extent.Y * (yMax - yMin);
            if (manipulateX) { xMin -= dx; xMax -= dx; }
            if (manipulateY) { yMin += dy; yMax += dy; }
        }
        if (hovered && io.MouseWheel != 0)
        {
            var factor = MathF.Pow(1.15f, -io.MouseWheel);
            var anchorX = xMin + (mouse.X - minimum.X) / extent.X * (xMax - xMin);
            var anchorY = yMax - (mouse.Y - minimum.Y) / extent.Y * (yMax - yMin);
            if (manipulateX) { var width = Math.Clamp((xMax - xMin) * factor, 1e-6f, 1e12f); var fraction = (anchorX - xMin) / (xMax - xMin); xMin = anchorX - width * fraction; xMax = xMin + width; }
            if (manipulateY) { var height = Math.Clamp((yMax - yMin) * factor, 1e-6f, 1e12f); var fraction = (anchorY - yMin) / (yMax - yMin); yMin = anchorY - height * fraction; yMax = yMin + height; }
        }
        CurvePoint? hit = null;
        var closestDistance = 64f * scale * scale;
        if (hovered)
            foreach (var point in points)
            {
                var position = Plot(point.Key, point.Component);
                if (!Finite(position)) continue;
                var distance = NVector2.DistanceSquared(position, mouse);
                if (!float.IsFinite(distance) || distance > closestDistance || hit != null && distance == closestDistance) continue;
                hit = point; closestDistance = distance;
            }
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && io.KeyAlt && !io.KeyCtrl && !io.KeyShift && !io.KeySuper)
        {
            if (track.Editable)
            {
                var value = Unplot(mouse);
                Run(() => track.Add(value.X, value.Y));
            }
            drag = null; boxStart = null;
        }
        else if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (!TryValidatePending()) return;
            if (hit != null)
            {
                if (!selected.Contains(hit.Value))
                {
                    if (uniformAxes) selected.Clear();
                    if (uniformAxes || io.KeyShift && !io.KeyCtrl && !io.KeyAlt && !io.KeySuper) selected.Add(hit.Value);
                }
                if (track.Editable && selected.Contains(hit.Value))
                {
                    drag = points.Where(point => selected.Contains(point.Value)).Select(point => (point.Value, point.Key, point.Component)).ToArray();
                    dragStart = Unplot(mouse); dragDelta = NVector2.Zero;
                }
            }
            else { if (uniformAxes) selected.Clear(); boxStart = mouse; }
        }
        if (drag != null)
        {
            if (Gui.IsKeyPressed(ImGuiKey.Escape) || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) { drag = null; dragDelta = NVector2.Zero; }
            else
            {
                dragDelta = Unplot(mouse) - dragStart;
                if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var edits = drag.Select(point => (point.Point, point.Key + dragDelta.X, point.Value + dragDelta.Y)).ToArray();
                    drag = null; dragDelta = NVector2.Zero; Run(() => track.Edit(edits));
                }
            }
        }
        if (boxStart is { } start && Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var low = NVector2.Min(start, mouse); var high = NVector2.Max(start, mouse);
            if (uniformAxes) selected.Clear();
            foreach (var point in points)
            {
                var position = Plot(point.Key, point.Component);
                if (!(position.X >= low.X && position.X <= high.X && position.Y >= low.Y && position.Y <= high.Y)) continue;
                if (uniformAxes || io.KeyShift && !io.KeyCtrl && !io.KeyAlt && !io.KeySuper) selected.Add(point.Value);
                else if (io.KeyCtrl && !io.KeyShift && !io.KeyAlt && !io.KeySuper) selected.Remove(point.Value);
            }
            boxStart = null;
        }
        if (Gui.BeginPopupContextItem("Curve actions"))
        {
            try
            {
            var value = Unplot(Gui.GetMousePosOnOpeningCurrentPopup());
            if (Gui.MenuItem(EditorLocalization.Widget("Add point", "Menu"), "Alt+Left click", false, track.Editable)) Run(() => track.Add(value.X, value.Y));
            if (Gui.MenuItem(EditorLocalization.Widget("Delete selected points", "Menu"), "Delete", false, track.Editable && selected.Count > 0)) Run(DeleteSelected);
            if (Gui.MenuItem(EditorLocalization.Widget("Clear whole curve", "Menu"), "", false, track.Editable))
                Run(() => { track.Delete(track.ReadPoints().Select(point => point.Value).ToArray()); ResetSelection(); });
            if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, selected.Count > 0)) Run(() => Gui.SetClipboardText(track.Copy(selected)));
            if (Gui.MenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, track.CanPaste(Gui.GetClipboardTextS() ?? ""))) Run(() => { var pasted = track.Paste(Gui.GetClipboardTextS() ?? ""); selected.Clear(); foreach (var point in pasted) selected.Add(point); });
            if (Gui.MenuItem(EditorLocalization.Widget("Fit to curve", "Menu"), "F")) Fit(0);
            if (Gui.MenuItem(EditorLocalization.Widget("Fit to curve height", "Menu"))) Fit(1);
            if (Gui.MenuItem(EditorLocalization.Widget("Fit to curve width", "Menu"))) Fit(-1);
            }
            finally { Gui.EndPopup(); }
        }
        if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive())
        {
            if (Gui.IsKeyPressed(ImGuiKey.Delete) && track.Editable) Run(DeleteSelected);
            if (Gui.IsKeyPressed(ImGuiKey.F)) FocusSelection();
            if (Gui.IsKeyPressed(ImGuiKey.Home)) Navigate(int.MinValue);
            if (Gui.IsKeyPressed(ImGuiKey.End)) Navigate(int.MaxValue);
            if (Gui.IsKeyPressed(ImGuiKey.LeftArrow)) Navigate(-1);
            if (Gui.IsKeyPressed(ImGuiKey.RightArrow)) Navigate(1);
            if (io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.C)) Run(() => Gui.SetClipboardText(track.Copy(selected)));
            if (io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.V) && track.Editable) Run(() => { var pasted = track.Paste(Gui.GetClipboardTextS() ?? ""); selected.Clear(); foreach (var point in pasted) selected.Add(point); });
        }
        var draw = Gui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xff222222);
        for (var tick = 0; tick <= 5; tick++)
        {
            var fraction = tick / 5f; var x = minimum.X + extent.X * fraction; var y = maximum.Y - extent.Y * fraction;
            draw.AddLine(new(x, minimum.Y), new(x, maximum.Y), 0xff444444);
            draw.AddLine(new(minimum.X, y), new(maximum.X, y), 0xff444444);
            draw.AddText(new(x - 12 * scale, maximum.Y + 4 * scale), 0xffaaaaaa, Unaxis(xMin + (xMax - xMin) * fraction, logX).ToString("G4"));
            draw.AddText(new(origin.X + 2 * scale, y - 6 * scale), 0xffaaaaaa, Unaxis(yMin + (yMax - yMin) * fraction, logY).ToString("G4"));
        }
        draw.PushClipRect(minimum, maximum, true);
        try
        {
        track.PrepareSampling();
        var samples = Math.Clamp((int)extent.X, 64, 1024);
        foreach (var sibling in DisplayChannels(track))
        {
            NVector2? previous = null;
            var activeChannel = ReferenceEquals(sibling, track) || track is CompoundCurveTrack || sibling is RotationCurveTrack && track is RotationCurveTrack;
            var color = ChannelColor(sibling.Channel, activeChannel);
            for (var index = 0; index <= samples; index++)
            {
                var key = Unaxis(xMin + (xMax - xMin) * index / samples, logX); var position = Plot(key, sibling.Sample(key));
                if (Finite(position)) { if (previous is { } before) draw.AddLine(before, position, color, (activeChannel ? 1.5f : 1) * scale); previous = position; } else previous = null;
            }
        }
        foreach (var point in points)
        {
            var delta = drag != null && selected.Contains(point.Value) ? dragDelta : NVector2.Zero;
            var position = Plot(point.Key + delta.X, point.Component + delta.Y);
            if (Finite(position))
            {
                draw.AddCircleFilled(position, 4 * scale, 0xffa09e5f);
                if (selected.Contains(point.Value)) draw.AddCircle(position, 8 * scale, 0xfff5f5f5, 0, scale);
            }
        }
        if (boxStart is { } box) { draw.AddRectFilled(NVector2.Min(box, mouse), NVector2.Max(box, mouse), 0x4466aaff); draw.AddRect(NVector2.Min(box, mouse), NVector2.Max(box, mouse), 0xff66aaff); }
        if (hovered && tracker) { draw.AddLine(new(mouse.X, minimum.Y), new(mouse.X, maximum.Y), 0x88777777); var value = Unplot(mouse); draw.AddText(mouse + new NVector2(10, 10) * scale, 0xffffffff, $"{value.X:G5}, {value.Y:G5}"); }
        }
        finally { draw.PopClipRect(); }
    }
    private static bool Finite(NVector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static uint ChannelColor(int channel, bool active)
    {
        var (r, g, b) = channel switch { 0 => (220, 78, 78), 1 => (101, 195, 106), 2 => (63, 116, 209), _ => (188, 188, 188) };
        if (!active) { r = r * 2 / 3; g = g * 2 / 3; b = b * 2 / 3; }
        return 0xff000000u | (uint)r | (uint)g << 8 | (uint)b << 16;
    }
}
