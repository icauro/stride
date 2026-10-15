using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private void SavePickerFilters() => host.SavePickerFilters(modalBrowser.CaptureFilters()
        .Where(filter => filter.Category != "Type" || !modalRequiredTypeFilters.Contains(filter.Value)).ToArray());

    private void DrawPickerFilterToolbar()
    {
        DrawPickerViewOptions(); Gui.SameLine();
        var filters = modalBrowser.CaptureFilters();
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - Gui.GetFrameHeight() - Gui.GetStyle().ItemSpacing.X));
        modalFilterSearch.Draw("##PickerFilter", ref modalSearch, modalBrowser.AvailableTypes,
            (choice, replace) => { modalBrowser.CommitFilter(choice, replace); SavePickerFilters(); }, categories: modalFilterCategories,
            measureText: host.MeasurePickerLabel, paintText: host.PaintPickerLabel);
        if (filters.Length > 0)
        {
            Gui.SameLine();
            if (Gui.Button("×###ClearPickerFilters"))
            {
                foreach (var name in modalBrowser.Names.ToArray()) modalBrowser.RemoveName(name);
                foreach (var tag in modalBrowser.Tags.ToArray()) modalBrowser.RemoveTag(tag);
                foreach (var type in filters.Where(filter => filter.Category == "Type" && !modalRequiredTypeFilters.Contains(filter.Value)))
                    if (modalBrowser.FindFilterType(type.Value) is { } actual) modalBrowser.ToggleType(actual);
                modalSearch = ""; modalFilterSearch.Reset(); SavePickerFilters();
            }
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Clear filters", "ToolTip"));
        }

        var width = Gui.GetContentRegionAvail().X; var first = true; var used = 0f;
        foreach (var filter in modalBrowser.CaptureFilters())
        {
            var readOnly = filter.Category == "Type" && modalRequiredTypeFilters.Contains(filter.Value);
            var type = filter.Category == "Type" ? modalBrowser.FindFilterType(filter.Value) : null;
            var label = type == null ? filter.Value : Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name;
            label = AssetBrowserPanel.EllipsizeTileLabel(label, 100 * Gui.GetStyle().FontScaleDpi, value => host.MeasurePickerLabel(value).X);
            var labelSize = host.MeasurePickerLabel(label); var textWidth = labelSize.X;
            var chipHeight = Math.Max(Gui.GetFrameHeight(), labelSize.Y + Gui.GetStyle().FramePadding.Y * 2);
            var labelWidth = textWidth + Gui.GetStyle().FramePadding.X * 2;
            var chipWidth = labelWidth + (readOnly ? 0 : chipHeight + Gui.GetStyle().ItemSpacing.X);
            if (!first && used + chipWidth < width) Gui.SameLine(); else used = 0;
            first = false; used += chipWidth + Gui.GetStyle().ItemSpacing.X;
            Gui.PushID(filter.Category + "/" + filter.Value);
            try
            {
                var origin = Gui.GetCursorScreenPos();
                using (ImGuiScopes.Disabled(readOnly))
                    if (Gui.Button("##Filter", new Vector2(labelWidth, chipHeight)))
                    {
                        if (filter.Category == "Name" && modalBrowser.Names.FirstOrDefault(value => value.Text == filter.Value) is { } name) modalBrowser.ToggleName(name);
                        else if (filter.Category == "Tag" && modalBrowser.Tags.FirstOrDefault(value => value.Text == filter.Value) is { } tag) modalBrowser.ToggleTag(tag);
                        else if (type != null) modalBrowser.ToggleTypeEnabled(type);
                        SavePickerFilters();
                    }
                host.PaintPickerLabel(label, origin + Gui.GetStyle().FramePadding, origin, origin + new Vector2(labelWidth, chipHeight),
                    Gui.GetColorU32(filter.Enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled));
                if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    Gui.BeginTooltip(); host.DrawPickerLabel(filter.Category.ToLowerInvariant() + ": " + (type == null ? filter.Value : Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name)); Gui.EndTooltip();
                }
                if (!readOnly)
                {
                    Gui.SameLine();
                    if (Gui.SmallButton("×"))
                    {
                        if (filter.Category == "Name" && modalBrowser.Names.FirstOrDefault(value => value.Text == filter.Value) is { } name) modalBrowser.RemoveName(name);
                        else if (filter.Category == "Tag" && modalBrowser.Tags.FirstOrDefault(value => value.Text == filter.Value) is { } tag) modalBrowser.RemoveTag(tag);
                        else if (type != null) modalBrowser.ToggleType(type);
                        SavePickerFilters();
                    }
                }
            }
            finally { Gui.PopID(); }
        }
    }
}
