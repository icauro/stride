namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void OpenPrefab(string path, bool reload = false) => OpenRetainedDocument(path, true, reload, true);
}
