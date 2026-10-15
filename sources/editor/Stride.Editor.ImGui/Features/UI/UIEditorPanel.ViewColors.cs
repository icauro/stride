using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed class UIAdornmentStyle(Color color)
    {
        internal NVector4 Color = Channels(color);
        internal float Thickness = 2;
    }
    private sealed class UIViewSettings
    {
        internal readonly UIAdornmentStyle Guideline = new(Color.Red);
        internal readonly UIAdornmentStyle Highlight = new(Color.Pink);
        internal readonly UIAdornmentStyle Selection = new(Color.LimeGreen);
        internal readonly UIAdornmentStyle Sizing = new(Color.Cyan);
        internal float SnapValue = 1;
        internal Guid[] SelectionIds = [];
        internal Guid? DisplayedRoot;
        internal bool WholeAssetProperties = true;
    }
    // These are native per-editor view preferences, not authored asset mutations.
    private readonly ConditionalWeakTable<UIDocument, UIViewSettings> uiViewSettings = new();
    private UIViewSettings ViewSettings => uiViewSettings.GetValue(document!, _ => new());
    private static NVector4 Channels(Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private void DrawViewColors()
    {
        if (host.DrawViewSettingsButton()) Gui.OpenPopup("##UIViewColors");
        if (!Gui.BeginPopup("##UIViewColors")) return;
        try
        {
            host.DrawViewLabel("Colors", true);
            if (!Gui.BeginTable("##UIAdornmentColors", 3, ImGuiTableFlags.SizingFixedFit)) return;
            try
            {
                Gui.TableSetupColumn("##Name");
                Gui.TableSetupColumn("##Color");
                Gui.TableSetupColumn("##Thickness", ImGuiTableColumnFlags.WidthFixed, 80 * Gui.GetStyle().FontScaleDpi);
                DrawViewColorRow("Guideline", ViewSettings.Guideline);
                DrawViewColorRow("Highlight", ViewSettings.Highlight);
                DrawViewColorRow("Selection", ViewSettings.Selection);
                DrawViewColorRow("Sizing", ViewSettings.Sizing);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.EndPopup(); }
    }

    private void DrawViewColorRow(string label, UIAdornmentStyle style)
    {
        Gui.PushID(label);
        try
        {
            Gui.TableNextRow(); Gui.TableNextColumn(); Gui.AlignTextToFramePadding(); host.DrawViewLabel(label);
            Gui.TableNextColumn();
            if (Gui.ColorButton("##Swatch", style.Color, ImGuiColorEditFlags.NoTooltip, new NVector2(16 * Gui.GetStyle().FontScaleDpi)))
                Gui.OpenPopup("##Color");
            if (Gui.BeginPopup("##Color"))
            {
                try
                {
                    var color = style.Color;
                    Gui.SetNextItemWidth(240 * Gui.GetStyle().FontScaleDpi);
                    if (Gui.ColorPicker4("##Picker", ref color, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoOptions))
                        style.Color = Channels(new Color(color.X, color.Y, color.Z, color.W));
                }
                finally { Gui.EndPopup(); }
            }
            Gui.TableNextColumn(); Gui.SetNextItemWidth(-1);
            var thickness = style.Thickness;
            if (Gui.InputFloat("##Thickness", ref thickness, 0, 0, "%.0f") && float.IsFinite(thickness))
                style.Thickness = Math.Max(1, thickness);
        }
        finally { Gui.PopID(); }
    }

    private float SizingHandleHalfSize => (Math.Max(8, ViewSettings.Sizing.Thickness * 4) + ViewSettings.Sizing.Thickness * 2)
        * viewport!.Zoom / 2;

    private void DrawCanvasHighlight(NVector2 origin, NVector2 mouse, bool hovered)
    {
        if (!hovered || factoryDrag != null || viewport?.Pick(mouse) is not { } id || selected.Contains(id)
            || !viewport.TryElementRect(id, out var minimum, out var maximum)) return;
        var style = ViewSettings.Highlight;
        var thickness = style.Thickness * viewport.Zoom;
        var offset = new NVector2(thickness / 2);
        Gui.GetWindowDrawList().AddRect(origin + minimum - offset, origin + maximum + offset,
            Gui.GetColorU32(style.Color), 0, ImDrawFlags.None, thickness);
    }
}
