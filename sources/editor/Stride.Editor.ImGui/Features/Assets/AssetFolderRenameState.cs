namespace Stride.Editor.ImGui;

internal sealed class AssetFolderRenameState
{
    internal string? Id;
    internal string Text = "";
    internal bool Focus;
    internal void Begin(AssetBrowserFolder folder) { Id = folder.Id; Text = folder.Name; Focus = true; }
    internal void Clear() { Id = null; Text = ""; Focus = false; }
}
