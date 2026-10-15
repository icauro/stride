namespace Stride.Editor.ImGui;

internal sealed partial class EditorSelectionService
{
    // Collection-change notifications must inspect real stored selections without
    // synchronizing/pruning projections in the middle of a graph or folder remap.
    internal string[] CaptureNativeHierarchyScopeIds()
        => NativePresentedOwners.SelectMany(source => source.SelectedIds.Select(id => source.Asset.Id + "/entity/" + id))
            .Concat(hierarchyContent.Select(item => item.EntityId is { } entity ? item.Document.Asset.Id + "/entity/" + entity
                : item.IsRoot ? item.Document.Asset.Id + "/root" : item.Document.Asset.Id + "/folder/" + item.FolderOwner + "/" + item.FolderPath))
            .Concat(selectedSceneRoots.Select(source => source.Asset.Id + "/root")).Distinct(StringComparer.Ordinal).ToArray();
}
