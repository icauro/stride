namespace Stride.Editor.ImGui;

internal sealed partial class EditorSelectionService
{
    internal IEnumerable<HierarchyContentItem> HierarchyClipboardFolders
    {
        get
        {
            var contents = HierarchyContent;
            foreach (var item in contents)
            {
                if (item.FolderPath == null) continue;
                // Native Copy computes common content roots separately for each asset.
                // A metadata root owns its whole folder tree, even while unloaded.
                if (item.Document is SceneDocument scene && selectedSceneRoots.Contains(scene)
                    || contents.Any(root => root.IsRoot && ReferenceEquals(root.Document, item.Document))) continue;
                yield return item;
            }
        }
    }
}
