namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SaveAssetBrowserFilters()
        => SavePickerAssetFilters(assetBrowser.CaptureFilters());
    private void SavePickerAssetFilters(IReadOnlyList<AssetFilterPreference> filters)
    {
        if (preferences == null) return;
        var previous = preferences.Current.AssetFilters;
        preferences.Current.AssetFilters = filters.ToList();
        if (preferences.Save()) return;
        preferences.Current.AssetFilters = previous;
        statusMessage = preferences.Error ?? "Unable to save editor preferences.";
        outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Editor preferences", statusMessage);
    }
}
