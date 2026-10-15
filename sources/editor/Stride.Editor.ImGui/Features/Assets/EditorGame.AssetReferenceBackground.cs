using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WeakReference<ProjectWorkspace>? referenceBackgroundWorkspace;
    private AssetEntry[] referenceBackgroundAssets = [];
    private long referenceBackgroundMainSelectionVersion;

    private bool IsReferenceBackgroundCurrent()
        => referenceBackgroundWorkspace != null && referenceBackgroundWorkspace.TryGetTarget(out var owner)
            && ReferenceEquals(workspace, owner) && referenceBackgroundMainSelectionVersion == assetBrowser.SelectionVersion
            && referenceBackgroundAssets.Select(asset => asset.Id).SequenceEqual(assetReferences.SelectedEntries.Select(asset => asset.Id))
            && referenceBackgroundAssets.All(asset => owner.AssetItems.Any(item => item.Id == asset.Id && !item.IsDeleted && SamePath(item.FullPath.ToOSPath(), asset.Path)));

    private void DrawReferenceBackgroundContext(ref Action? commandAction)
    {
        var disabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0;
        if (!Gui.BeginPopupContextWindow("Reference background actions", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) return;
        try
        {
            if (Gui.IsWindowAppearing() && workspace is { } owner)
            {
                referenceBackgroundWorkspace = new(owner);
                referenceBackgroundAssets = assetReferences.SelectedEntries;
                referenceBackgroundMainSelectionVersion = assetBrowser.SelectionVersion;
            }
            if (!IsReferenceBackgroundCurrent() || disabled || Gui.GetIO().AppFocusLost)
            { Gui.CloseCurrentPopup(); return; }
            if (DrawReferenceAssetActions(referenceBackgroundAssets) is { } action)
                commandAction = () => { if (IsReferenceBackgroundCurrent()) action(); };
        }
        finally { Gui.EndPopup(); }
    }
}
