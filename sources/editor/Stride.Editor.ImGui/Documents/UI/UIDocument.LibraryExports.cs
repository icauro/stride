using Stride.UI.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.UI;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    private bool synchronizingLibraryExports;
    private bool CanSynchronizeLibraryExports => !synchronizingLibraryExports && CanSave && Asset is UILibraryAsset
        && SessionHistory is { IsCapturingOperations: true, IsCompensating: false } history && !history.Stack.RollInProgress
        && history.Registry.Find(Asset.Id) is { IsPublished: true, IsDisposed: false } owner && ReferenceEquals(owner.Document, this);
    private void OnUIHierarchyMemberChanged(object? sender, AssetMemberNodeChangeEventArgs change)
    {
        layoutVersion++;
        if (!CanSynchronizeLibraryExports || change.Member.Name != nameof(UIElement.Name)
            || change.Member.Parent.Retrieve() is not UIElement element
            || !Roots.Any(root => ReferenceEquals(root, element))) return;
        SynchronizeLibraryExport(element.Id, element.Name);
    }
    private void OnUIHierarchyItemChanged(object? sender, AssetItemNodeChangeEventArgs change)
    {
        layoutVersion++;
        if (!CanSynchronizeLibraryExports || !ReferenceEquals(change.Collection.Retrieve(), Asset.Hierarchy.RootParts)) return;
        // Original UILibraryEditor observes actual root Add/Remove and their Name;
        // it does not normalize saved dictionaries merely by opening an asset.
        if (change.OldValue is UIElement removed && change.ChangeType is ContentChangeType.CollectionRemove or ContentChangeType.CollectionUpdate)
            SynchronizeLibraryExport(removed.Id, null);
        if (change.NewValue is UIElement added && change.ChangeType is ContentChangeType.CollectionAdd or ContentChangeType.CollectionUpdate)
            SynchronizeLibraryExport(added.Id, added.Name);
    }
    private void SynchronizeLibraryExport(Guid id, string? name)
    {
        var library = (UILibraryAsset)Asset;
        var exports = ObjectNode(library)[nameof(UILibraryAsset.PublicUIElements)].Target!;
        var index = new NodeIndex(id);
        synchronizingLibraryExports = true;
        try
        {
            // These exact native node updates are captured by the caller's active
            // canonical journal. No nested/shadow history is created by this adapter.
            if (string.IsNullOrWhiteSpace(name))
            {
                if (library.PublicUIElements.TryGetValue(id, out var previous)) exports.Remove(previous, index);
            }
            else if (!library.PublicUIElements.TryGetValue(id, out var previous)) exports.Add(name, index);
            else if (!StringComparer.Ordinal.Equals(previous, name)) exports.Update(name, index);
        }
        finally { synchronizingLibraryExports = false; }
    }
}
