namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private PickerContextCapabilities PickerSelectionCapabilities(AssetBrowserContent row)
    {
        var selection = modalBrowser.SelectedContent;
        if (!selection.Any(selected => selected.Id == row.Id)) return new(false, false, false, false, false);
        var content = selection.Count == 1 ? host.ContextCapabilities(selection[0]) : null;
        return new(content?.Rename == true, selection.Count != 0 && selection.All(selected => host.ContextCapabilities(selected).Delete),
            content?.Asset == true, content?.HasSource == true,
            selection.Count != 0 && host.ContextCapabilities(selection[^1]).Explore);
    }

    private AssetBrowserContent PickerContextTarget(AssetBrowserContent row, PickerContextAction action)
    {
        if (action is not (PickerContextAction.OpenAsset or PickerContextAction.OpenText or PickerContextAction.OpenSource)) return row;
        var selection = modalBrowser.SelectedContent;
        return selection.Count == 1 && selection[0].Asset != null ? selection[0] : row;
    }
}
