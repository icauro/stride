using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void AddProceduralDefaultCollectionItem(ProceduralAssetDocument asset, ProceduralCollectionContext context, string key)
        => Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset) || !asset.CanAddCollection(context))
                throw new InvalidOperationException("The procedural collection changed.");
            var dictionary = context.Node.Descriptor as DictionaryDescriptor;
            if (dictionary == null && asset.CollectionNativeEntries(context) != null)
                throw new InvalidOperationException("The native collection now requires a supplied factory entry.");
            var target = asset.AcquireCollectionTarget(context); var transferred = false;
            try
            {
                var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name ==
                    (dictionary != null ? "AddPrimitiveKey" : "AddNewItem"));
                var actualKey = dictionary == null ? null : NativeDictionaryKey.CreateForAdd(dictionary.KeyType, key);
                if (command != null)
                {
                    StartEditorExtensionCommand(asset, target!, command, actualKey,
                        () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                    transferred = true;
                }
                else
                {
                    if (dictionary == null) asset.AddDefaultCollectionItem(context);
                    else
                    {
                        var value = Stride.Core.Assets.AssetCollectionCommandRules.IsReferenceType(context.ElementType) ? null : ObjectFactoryRegistry.NewInstance(context.ElementType);
                        asset.AddCollectionItem(context, value, actualKey);
                    }
                }
            }
            finally { if (!transferred) target?.Dispose(); }
        });
    private void SelectProceduralCollectionEntry(ProceduralAssetDocument asset, ProceduralCollectionContext context, AbstractNodeEntry entry, string key)
        => Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset) || !asset.CanAddCollection(context) || !NativeAbstractEntries.Contains(asset.CollectionEntries(context), entry))
                throw new InvalidOperationException("The native collection factory entry is no longer available.");
            var target = asset.AcquireCollectionTarget(context); var transferred = false;
            try
            {
                var command = context.Node.Descriptor is DictionaryDescriptor ? null : target?.Presenter.Commands.FirstOrDefault(command => command.Name == "AddNewItem");
                if (command != null)
                {
                    StartEditorExtensionCommand(asset, target!, command, entry, () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                    transferred = true;
                }
                else
                {
                    var actualKey = context.Node.Descriptor is DictionaryDescriptor dictionary ? NativeDictionaryKey.CreateForAdd(dictionary.KeyType, key) : null;
                    asset.AddCollectionItem(context, entry.GenerateValue(null!), actualKey);
                }
            }
            finally { if (!transferred) target?.Dispose(); }
        });
    private void SelectProceduralCollectionItemEntry(ProceduralAssetDocument asset, ProceduralCollectionItem item, AbstractNodeEntry entry)
        => Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset) || !asset.CanEditCollectionItem(item) || !NativeAbstractEntries.Contains(asset.CollectionItemEntries(item), entry))
                throw new InvalidOperationException("The native item factory entry is no longer available.");
            var target = asset.AcquireCollectionItemTarget(item); var transferred = false;
            try
            {
                var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
                if (command != null)
                {
                    StartEditorExtensionCommand(asset, target!, command, entry, () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                    transferred = true;
                }
                else if (!entry.IsMatchingValue(item.Value)) asset.UpdateCollectionItem(item, item.Value, entry.GenerateValue(item.Value));
            }
            finally { if (!transferred) target?.Dispose(); }
        });
}
