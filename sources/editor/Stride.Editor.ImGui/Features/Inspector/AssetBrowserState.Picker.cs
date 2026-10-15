namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    internal void BindPicker(ProjectWorkspace workspace)
    {
        Bind(workspace);
        var changed = false;
        foreach (var item in workspace.AssetItems.Where(item => !item.IsDeleted)) changed |= revealedAssets.Add(item.Id);
        if (changed) RefreshEntries(workspace);
    }
}
