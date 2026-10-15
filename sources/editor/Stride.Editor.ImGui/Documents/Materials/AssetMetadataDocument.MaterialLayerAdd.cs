using Stride.Assets.Materials;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering.Materials;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IEnumerable<AbstractNodeEntry>? MaterialLayerAddEntries => ExtensionCollectionAbstractEntries(MaterialLayerCollection);
    internal EditorPresenterTarget? AcquireMaterialLayerAddTarget()
        => AcquireExtensionCommandTarget(AuthoringGraph.RootNode[nameof(MaterialAsset.Layers)]);
    internal bool CanAddMaterialLayer()
    {
        if (MaterialSettings == null || !CanEditMaterialSettings) return false;
        var member = AuthoringGraph.RootNode[nameof(MaterialAsset.Layers)];
        var rules = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(member.MemberDescriptor.MemberInfo);
        return rules?.ReadOnly != true && !IsExtensionPropertyReadOnly(member) && CanEditAssetSettingNode(MaterialLayerCollection)
            && NativePropertyClipboard.HasAdd(MaterialLayerCollection);
    }
    internal void AddMaterialLayerEntry(AbstractNodeEntry? entry)
    {
        CommitEdits();
        if (!CanAddMaterialLayer()) throw new InvalidOperationException("The material layer collection is read-only.");
        var entries = MaterialLayerAddEntries;
        if (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry))
            throw new InvalidOperationException("The material layer factory changed.");
        var value = entry == null ? AssetCollectionCommandRules.CreateItemInstance(typeof(MaterialBlendLayer)) : entry.GenerateValue(null!);
        if (value is not MaterialBlendLayer layer) throw new ArgumentException("The material layer entry has an incompatible value.");
        AssetCollectionItemIdHelper.GenerateMissingItemIds(layer);
        if (!CanAddMaterialLayer()) throw new InvalidOperationException("The material layer collection changed during construction.");
        var collection = MaterialLayerCollection;
        ApplyInheritedAssetMutation(() => collection.Add(layer, new NodeIndex(MaterialSettings!.Layers.Count)), "Add material layer");
    }
}
