using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private readonly AssetFilterSearch assetFilterSearch = new();
    private void DrawAssetToolbar()
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new NVector2(3, 3) * dpi);
        Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NVector2(4, 4) * dpi);
        try
        {
            using (ImGuiScopes.Disabled(!host.CanAddAssets))
                if (host.DrawButton("+ " + EditorLocalization.Text("Add asset") + "###+ Add asset")) host.RequestAddition();
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Add an asset to this location", "ToolTip"));
            Gui.SameLine();
            Gui.BeginDisabled(!host.CanEditSelectedContent);
            if (host.Icons.Button("rename", EditorLocalization.Text("Open asset in dedicated editor", "ToolTip") + " (Ctrl+Enter)", 16)) host.EditSelectedContent();
            if (host.CaptureControls) host.RecordControl("asset/edit");
            Gui.EndDisabled(); Gui.SameLine();
            var modified = host.ModifiedSourceCount;
            using (ImGuiScopes.Disabled(!host.CanUpdateModifiedSources))
                if (host.Icons.Button("asset-update-source", EditorLocalization.Text("Update assets whose source files have changed", "ToolTip") + " (Ctrl+R)", 16)) host.UpdateModifiedSources();
            if (modified > 0) { Gui.SameLine(); host.DrawLabel($"({modified})"); }
            Gui.SameLine();
            var effects = host.PendingEffectCount;
            using (ImGuiScopes.Disabled(effects == 0 || !host.CanImportEffectLog))
                if (host.Icons.Button("reimport-effects", EditorLocalization.Text("Import last effects compiled remotely", "ToolTip"), 16)) host.ImportEffectLog();
            if (effects > 0) { Gui.SameLine(); host.DrawLabel($"({effects})"); }
            Gui.SameLine();
            if (host.Icons.Button("scene-toolbar-view", EditorLocalization.Text("Asset view options", "ToolTip"), 16)) Gui.OpenPopup("Asset view options");
            if (host.CaptureControls) host.RecordControl("asset/options");
            Gui.SameLine(); Gui.SetNextItemWidth(120 * dpi);
            assetFilterSearch.Draw("##AssetFilter", ref assetFilter, assetBrowser.AvailableTypes,
                (choice, replace) => { assetBrowser.CommitFilter(choice, replace); host.SaveFilters(); },
                () => { if (host.CaptureControls) host.RecordControl("asset/filter"); },
                measureText: host.MeasureLabel, paintText: host.PaintLabel);
            Gui.SameLine();
            if (Gui.ArrowButton("##AssetFilterTypes", ImGuiDir.Down)) Gui.OpenPopup("Asset filters");
            if (Gui.BeginPopup("Asset filters"))
            {
                if (host.BeginMenu("Type"))
                {
                    foreach (var type in assetBrowser.AvailableTypes)
                    {
                        Gui.PushID(type.FullName);
                        if (host.DrawMenuItem(Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name, "", assetBrowser.HasType(type))) { assetBrowser.ToggleType(type); host.SaveFilters(); }
                        Gui.PopID();
                    }
                    Gui.EndMenu();
                }
                if (host.BeginMenu("Tag"))
                {
                    Gui.SetNextItemWidth(160 * dpi); Gui.InputTextWithHint("##TagFilter", "Tag", ref tagFilterDraft, 256);
                    Gui.SameLine();
                    if (host.DrawButton("Add")) { assetBrowser.AddTag(tagFilterDraft); tagFilterDraft = ""; host.SaveFilters(); }
                    Gui.EndMenu();
                }
                Gui.EndPopup();
            }
            if (HasAssetFilters)
            {
                Gui.SameLine();
                if (host.Icons.Button("delete-entity", EditorLocalization.Text("Clear filters", "ToolTip"), 16)) { assetFilter = tagFilterDraft = ""; assetBrowser.ClearFilters(); host.SaveFilters(); }
                if (host.CaptureControls) host.RecordControl("asset/filter/clear");
            }
            DrawAssetOptions();
        }
        finally { Gui.PopStyleVar(2); }
        DrawFilterChips();
    }

    private bool HasAssetFilters => assetBrowser.Names.Count > 0 || assetBrowser.TypeFilterCount > 0 || assetBrowser.Tags.Count > 0;

    private void DrawFilterChips()
    {
        if (!HasAssetFilters) return;
        var width = Gui.GetContentRegionAvail().X;
        var first = true;
        var usedWidth = 0f;
        bool Chip(string id, string label, string tooltip, bool enabled, Action? toggle)
        {
            var text = EllipsizeNativeTileLabel(label, 100 * Gui.GetStyle().FontScaleDpi);
            var textSize = host.MeasureLabel(text);
            var buttonSize = new NVector2(textSize.X + Gui.GetStyle().FramePadding.X * 2,
                Math.Max(Gui.GetFrameHeight(), textSize.Y + Gui.GetStyle().FramePadding.Y * 2));
            var removeWidth = host.MeasureLabel("×").X + Gui.GetStyle().FramePadding.X * 2;
            var chipWidth = buttonSize.X + removeWidth + Gui.GetStyle().ItemSpacing.X * 2;
            if (!first && usedWidth + chipWidth < width) Gui.SameLine();
            else usedWidth = 0;
            usedWidth += chipWidth;
            first = false;
            Gui.PushID(id);
            if (!enabled) Gui.PushStyleColor(ImGuiCol.Text, Gui.GetColorU32(ImGuiCol.TextDisabled));
            var origin = Gui.GetCursorScreenPos();
            var clicked = Gui.Button("##FilterLabel", buttonSize);
            var innerMinimum = origin + Gui.GetStyle().FramePadding;
            var innerMaximum = Gui.GetItemRectMax() - Gui.GetStyle().FramePadding;
            host.PaintLabel(text, innerMinimum + NVector2.Max(NVector2.Zero, (innerMaximum - innerMinimum - textSize) * Gui.GetStyle().ButtonTextAlign),
                innerMinimum, innerMaximum, Gui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled));
            if (Gui.IsItemHovered()) { Gui.BeginTooltip(); host.DrawLabel(tooltip); Gui.EndTooltip(); }
            if (clicked && toggle != null) toggle();
            if (!enabled) Gui.PopStyleColor();
            Gui.SameLine(); var remove = host.DrawSmallButton("×"); Gui.PopID();
            return remove;
        }
        foreach (var preference in assetBrowser.CaptureFilters())
        {
            switch (preference.Category)
            {
                case "Name":
                {
                    var filter = assetBrowser.Names.FirstOrDefault(value => value.Text == preference.Value);
                    if (filter != null && Chip("name/" + filter.Text, filter.Text, "name: " + filter.Text, filter.Enabled, () => { assetBrowser.ToggleName(filter); host.SaveFilters(); }))
                    { assetBrowser.RemoveName(filter); host.SaveFilters(); }
                    break;
                }
                case "Type":
                {
                    var type = assetBrowser.FindFilterType(preference.Value);
                    if (type != null && Chip(type.FullName!, Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name, "type: " + (Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name), assetBrowser.IsTypeEnabled(type), () => { assetBrowser.ToggleTypeEnabled(type); host.SaveFilters(); }))
                    { assetBrowser.ToggleType(type); host.SaveFilters(); }
                    break;
                }
                case "Tag":
                {
                    var tag = assetBrowser.Tags.FirstOrDefault(value => value.Text == preference.Value);
                    if (tag != null && Chip("tag/" + tag.Text, tag.Text, "tag: " + tag.Text, tag.Enabled, () => { assetBrowser.ToggleTag(tag); host.SaveFilters(); }))
                    { assetBrowser.RemoveTag(tag); host.SaveFilters(); }
                    break;
                }
            }
        }
    }
}
