using Stride.UI.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal bool CanInstantiateFactory(UIElementFactory factory, Guid? parentId, int index, Func<Stride.Core.Assets.AssetId, UIDocument?> resolve)
    {
        if (!CanSave || SessionHistory == null || !ReferenceEquals(SessionHistory, factory.Library.SessionHistory)
            || factory.Library.Asset is not UILibraryAsset library || !library.PublicUIElements.ContainsKey(factory.ElementId)
            || !library.Hierarchy.RootParts.Any(root => root.Id == factory.ElementId)) return false;
        if (SessionHistory.Registry.Find(Asset.Id) is not { IsPublished: true, IsDisposed: false } destinationOwner
            || !ReferenceEquals(destinationOwner.Document, this)
            || SessionHistory.Registry.Find(library.Id) is not { IsPublished: true, IsDisposed: false } libraryOwner
            || !ReferenceEquals(libraryOwner.Document, factory.Library)) return false;
        var parent = parentId is { } id ? Find(id) : null;
        if (parentId != null && parent == null || !CanInsert(parent)
            || index < 0 || index > (parent?.VisualChildren.Count ?? Roots.Count)) return false;
        if (factory.SystemLibrary) return true;
        return factory.Library.Asset.Id != Asset.Id
            && !factory.Library.HasHierarchyLibraryDependency(Asset.Id, [factory.ElementId], resolve);
    }
    internal Guid InstantiateFactory(UIElementFactory factory, Guid? parentId, int index, Func<Stride.Core.Assets.AssetId, UIDocument?> resolve)
    {
        CommitEdits(); factory.Library.CommitEdits();
        if (!CanInstantiateFactory(factory, parentId, index, resolve))
            throw new InvalidOperationException("The selected UI factory cannot be inserted at this location.");
        return Insert(CreateLibraryInstance(factory.Library, factory.ElementId, factory.Url, factory.SystemLibrary), parentId, index);
    }
}
