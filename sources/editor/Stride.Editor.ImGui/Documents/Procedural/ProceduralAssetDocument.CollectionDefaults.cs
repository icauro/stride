using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal void AddDefaultCollectionItem(ProceduralCollectionContext context)
    {
        if (!CanAddCollection(context) || context.Node.Descriptor is not CollectionDescriptor
            || !AssetCollectionCommandRules.CanAdd(context.ElementType) || CollectionNativeEntries(context) != null)
            throw new InvalidOperationException("The default collection command changed.");
        var instance = ObjectFactoryRegistry.NewInstance(context.ElementType);
        var accessor = context.Parent is { } parent ? new NodeAccessor(parent.Context.Node, parent.Index)
            : new NodeAccessor(context.Origin.Node, NodeIndex.Empty);
        var value = AssetCollectionCommandRules.IsReferenceType(context.ElementType) || AuthoringGraph.Definition.IsObjectReference(accessor, instance)
            ? null : instance;
        if (!CanAddCollection(context) || CollectionNativeEntries(context) != null)
            throw new InvalidOperationException("The collection changed during native default construction.");
        AddCollectionItem(context, value);
    }
}
