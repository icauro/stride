using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class UiControls
{
    public bool Enabled { get; set; }
    internal Action<string>? CaptureRecorder { get; set; }
    public readonly Dictionary<string, Vector2> Centers = [];
    public readonly Dictionary<string, uint> Viewports = [];
    public readonly Dictionary<string, bool> Expanded = [];
    internal readonly Dictionary<string, uint> NativeIDs = [];
    internal readonly Dictionary<string, int> NativeFrames = [];
    public unsafe void RecordDocumentTab(string id)
    {
        if (!Enabled) return;
        var bar = ImGuiP.GetCurrentTabBar();
        var window = ImGuiP.GetCurrentWindowRead();
        if (bar.IsNull || window.IsNull) { Centers.Remove(id); Viewports.Remove(id); NativeIDs.Remove(id); NativeFrames.Remove(id); return; }
        var minimum = Vector2.Max(Gui.GetItemRectMin(), window.ClipRect.Min);
        var maximum = Vector2.Min(Gui.GetItemRectMax(), window.ClipRect.Max);
        minimum = Vector2.Max(minimum, bar.BarRect.Min);
        maximum = Vector2.Min(maximum, bar.BarRect.Max);
        minimum.X = Math.Max(minimum.X, bar.ScrollingRectMinX);
        maximum.X = Math.Min(maximum.X, bar.ScrollingRectMaxX);
        if (maximum.Y > minimum.Y && maximum.X - minimum.X >= Gui.GetFrameHeight() * 2)
        {
            Centers[id] = (minimum + maximum) / 2; Viewports[id] = Gui.GetWindowViewport().ID;
            NativeIDs[id] = Gui.GetItemID(); NativeFrames[id] = Gui.GetFrameCount(); CaptureRecorder?.Invoke(id);
        }
        else { Centers.Remove(id); Viewports.Remove(id); NativeIDs.Remove(id); NativeFrames.Remove(id); }

        // Exact native v1.92.2b TabBarScrollingButtons geometry. BarRect.Max.X
        // already excludes two arrow widths plus one pixel after native layout.
        // These are observations of existing buttons, never selection operations.
        var arrowWidth = bar.BarRect.Max.Y - bar.BarRect.Min.Y - bar.FramePadding.Y * 2 - 2;
        for (var direction = 0; direction < 2; direction++)
        {
            var key = "document-tab-scroll/" + bar.ID + (direction == 0 ? "/left" : "/right");
            if (!bar.ScrollButtonEnabled || arrowWidth <= 0)
            { Centers.Remove(key); Viewports.Remove(key); NativeIDs.Remove(key); NativeFrames.Remove(key); continue; }
            var arrowMin = new Vector2(bar.BarRect.Max.X + 1 + direction * arrowWidth, bar.BarRect.Min.Y);
            var arrowMax = arrowMin + new Vector2(arrowWidth, bar.BarRect.Max.Y - bar.BarRect.Min.Y);
            arrowMin = Vector2.Max(arrowMin, window.ClipRect.Min); arrowMax = Vector2.Min(arrowMax, window.ClipRect.Max);
            if (arrowMax.X <= arrowMin.X || arrowMax.Y <= arrowMin.Y)
            { Centers.Remove(key); Viewports.Remove(key); NativeIDs.Remove(key); NativeFrames.Remove(key); continue; }
            Centers[key] = (arrowMin + arrowMax) / 2; Viewports[key] = Gui.GetWindowViewport().ID;
            NativeIDs[key] = ImGuiP.GetIDWithSeed(direction == 0 ? "##<" : "##>", (byte*)null, bar.ID);
            NativeFrames[key] = Gui.GetFrameCount(); CaptureRecorder?.Invoke(key);
        }
    }
    public unsafe void Record(string id)
    {
        if (!Enabled) return;
        if ((ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0)
        { Centers.Remove(id); Viewports.Remove(id); return; }
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var window = ImGuiP.GetCurrentWindowRead();
        if (!window.IsNull)
        {
            // IsItemVisible only proves intersection. A partly clipped tile's
            // full center may lie outside the actual window/framebuffer.
            minimum = Vector2.Max(minimum, window.ClipRect.Min);
            maximum = Vector2.Min(maximum, window.ClipRect.Max);
        }
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y) { Centers.Remove(id); Viewports.Remove(id); return; }
        Centers[id] = (minimum + maximum) * 0.5f;
        Viewports[id] = Gui.GetWindowViewport().ID;
        CaptureRecorder?.Invoke(id);
    }
}
