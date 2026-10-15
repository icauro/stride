using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SaveReferenceFilters()
    {
        if (preferences == null) return;
        var previous = preferences.Current.ReferenceFilters;
        preferences.Current.ReferenceFilters = assetReferences.CaptureFilters().ToList();
        if (preferences.Save()) return;
        preferences.Current.ReferenceFilters = previous;
        statusMessage = preferences.Error ?? "Unable to save editor preferences.";
        outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Editor preferences", statusMessage);
    }
    private void DrawReferenceFilterChips()
    {
        if (assetReferences.Names.Count == 0 && assetReferences.TypeFilterCount == 0 && assetReferences.Tags.Count == 0) return;
        var width = Gui.GetContentRegionAvail().X; var first = true; var usedWidth = 0f;
        bool Chip(string id, string label, bool enabled, Action toggle)
        {
            var labelWidth = Gui.CalcTextSize(label).X + Gui.GetStyle().FramePadding.X * 2;
            var chipWidth = labelWidth + Gui.GetFrameHeight() + Gui.GetStyle().ItemSpacing.X * 2;
            if (!first && usedWidth + chipWidth < width) Gui.SameLine();
            else usedWidth = 0;
            usedWidth += chipWidth; first = false;
            Gui.PushID(id);
            if (!enabled) Gui.PushStyleColor(ImGuiCol.Text, Gui.GetColorU32(ImGuiCol.TextDisabled));
            var origin = Gui.GetCursorScreenPos();
            if (Gui.Button("##FilterLabel", new NVector2(labelWidth, Gui.GetFrameHeight()))) { toggle(); SaveReferenceFilters(); }
            Gui.GetWindowDrawList().AddText(origin + Gui.GetStyle().FramePadding, Gui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled), label);
            if (!enabled) Gui.PopStyleColor();
            Gui.SameLine(); var remove = Gui.SmallButton("×"); Gui.PopID();
            return remove;
        }
        foreach (var preference in assetReferences.CaptureFilters())
        {
            switch (preference.Category)
            {
                case "Name":
                {
                    var name = assetReferences.Names.FirstOrDefault(value => value.Text == preference.Value);
                    if (name != null && Chip("ReferenceName/" + name.Text, "name: " + name.Text, name.Enabled, () => assetReferences.ToggleName(name)))
                    { assetReferences.RemoveName(name); SaveReferenceFilters(); }
                    break;
                }
                case "Type":
                {
                    var type = assetReferences.FindFilterType(preference.Value);
                    if (type != null && Chip("ReferenceType/" + type.FullName, "type: " + (Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name), assetReferences.IsTypeEnabled(type), () => assetReferences.ToggleTypeEnabled(type)))
                    { assetReferences.ToggleType(type); SaveReferenceFilters(); }
                    break;
                }
                case "Tag":
                {
                    var tag = assetReferences.Tags.FirstOrDefault(value => value.Text == preference.Value);
                    if (tag != null && Chip("ReferenceTag/" + tag.Text, "tag: " + tag.Text, tag.Enabled, () => assetReferences.ToggleTag(tag)))
                    { assetReferences.RemoveTag(tag); SaveReferenceFilters(); }
                    break;
                }
            }
        }
    }
}
