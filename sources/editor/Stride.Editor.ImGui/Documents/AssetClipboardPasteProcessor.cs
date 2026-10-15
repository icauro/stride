using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel.CopyPasteProcessors;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Yaml;
using Stride.Core.Extensions;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

/// <summary>The native AssetItemPasteProcessor policy with session lookup supplied by the host.</summary>
internal sealed class AssetClipboardPasteProcessor(Func<AssetId, bool> exists,
    IReadOnlyCollection<IAssetPostPasteProcessor> postProcessors) : PasteProcessorBase
{
    public override bool Accept(Type targetRootType, Type targetMemberType, Type pastedDataType)
        => typeof(ICollection<AssetItem>).IsAssignableFrom(targetRootType)
            && typeof(AssetItem).IsAssignableFrom(TypeDescriptorFactory.Default.Find(pastedDataType).GetInnerCollectionType());

    public override bool ProcessDeserializedData(AssetPropertyGraphContainer graphContainer, object targetRootObject,
        Type targetMemberType, ref object data, bool isRootDataObjectReference, AssetId? sourceId,
        YamlAssetMetadata<OverrideType> overrides, YamlAssetPath basePath)
    {
        if (data is not IList<AssetItem> collection) return false;
        for (var index = 0; index < collection.Count; index++)
        {
            var item = collection[index];
            if (!exists(item.Id)) continue;
            var clone = AssetCloner.Clone(item.Asset, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects, out var remapping);
            var type = item.Asset.GetType();
            if (type.HasInterface(typeof(AssetCompositeHierarchy<,>)))
            {
                var path = basePath.Clone();
                path.PushItemId(CollectionItemIdHelper.GetCollectionItemIds(collection)[index]);
                AssetCloningHelper.RemapIdentifiablePaths(overrides, remapping, path);
                dynamic composite = clone;
                AssetPartsAnalysis.GenerateNewBaseInstanceIds(composite.Hierarchy);
            }
            postProcessors.FirstOrDefault(processor => processor.Accept(type))?.PostPasteDeserialization(clone);
            collection[index] = new AssetItem(item.Location, clone);
        }
        data = collection;
        return true;
    }
}
