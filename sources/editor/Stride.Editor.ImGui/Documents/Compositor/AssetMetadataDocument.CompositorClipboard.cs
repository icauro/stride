using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Reflection;
using Stride.Core.Quantum;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private NativePropertyClipboard? compositorClipboard;
    private NativePropertyClipboard CompositorClipboard => compositorClipboard ??= new(AuthoringGraph, AuthoredAsset,
        member => CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member),
        (collection, index) => collection is IAssetObjectNode node && CanEditAssetSettingItem(node, index),
        collection => collection is IAssetObjectNode node && CanEditAssetSettingNode(node) && NativePropertyClipboard.HasAdd(collection),
        collection => NativePropertyClipboard.HasRemove(collection),
        NormalizeCompositorClipboardMember, NormalizeCompositorClipboardItem, UpdateCompositorClipboardContext, AcceptCompositorClipboardItem);
    public bool CanCopyCompositorProperty(CompositorProperty property) => CompositorAsset != null
        && compositorPropertyMembers.Contains(property) && (property.Accessor.IsItem
            ? property.Accessor.Node is IAssetObjectNode collection && collection.HasId(property.ItemId)
                && collection.IdToIndex(property.ItemId) == property.Accessor.Index
            : property.Accessor.Node is IMemberNode member && authoringNodes.Contains((IAssetNode)member));
    public string CopyCompositorProperty(CompositorProperty property)
    {
        CommitEdits();
        return CanCopyCompositorProperty(property) ? CompositorClipboard.Copy(property.Accessor) : "";
    }
    private static Type CompositorPasteType(CompositorProperty property) => property.ParentCollection?.Collection?.Type ?? property.DeclaredType;
    public bool CanPasteCompositorProperties(IReadOnlyList<CompositorProperty> properties, string text)
    {
        if (!CanEditCompositor || properties.Count == 0) return false;
        foreach (var property in properties)
        {
            if (!CanCopyCompositorProperty(property) || property.Rules?.ReadOnly == true || property.ParentCollection?.Rules?.ReadOnly == true) return false;
            if (property.Collection is { } collection)
            {
                if (!CanEditAssetSettingNode(collection)) return false;
            }
            else if (!CanEditCompositorProperty(property)) return false;
            if (!CompositorClipboard.CanPaste(text, CompositorPasteType(property))) return false;
        }
        return true;
    }
    public void PasteCompositorProperties(IReadOnlyList<CompositorProperty> properties, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteCompositorProperties(properties, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these compositor properties.");
        var targets = properties.DistinctBy(property => property.Accessor).ToArray();
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var property in targets)
            {
                var accessor = property.Accessor;
                if (accessor.IsItem && accessor.Node is IAssetObjectNode collection)
                {
                    if (!collection.HasId(property.ItemId)) throw new InvalidOperationException("The destination clipboard item no longer exists.");
                    accessor = new NodeAccessor(collection, collection.IdToIndex(property.ItemId));
                }
                CompositorClipboard.Paste(text, CompositorPasteType(property), accessor, replace);
            }
        }, replace ? "Replace property" : "Paste property");
    }
    private object NormalizeCompositorClipboardMember(IMemberNode member, object value)
    {
        var property = compositorPropertyMembers.FirstOrDefault(field => field.Accessor.IsMember && ReferenceEquals(field.Accessor.Node, member));
        if (property != null && ContentAssetReference.Supports(property.Type)) ContentAssetReference.Validate(property.Type, value, property.AllowsNull);
        return property == null ? value : NormalizeCompositorProperty(property, value)!;
    }
    private object NormalizeCompositorClipboardItem(IObjectNode collection, NodeIndex index, object value)
    {
        var property = compositorPropertyMembers.FirstOrDefault(field => field.Accessor.IsItem
            && ReferenceEquals(field.Accessor.Node, collection) && field.Accessor.Index == index);
        return property == null ? value : NormalizeCompositorProperty(property, value)!;
    }
    private bool AcceptCompositorClipboardItem(IObjectNode collection, NodeIndex index, object value)
    {
        var property = compositorPropertyMembers.FirstOrDefault(field => ReferenceEquals(field.Collection, collection)
            || field.Accessor.Node is IMemberNode member && ReferenceEquals(member.Target, collection));
        var rules = property?.Rules;
        if (rules == null && property?.Accessor.Node is IMemberNode owner)
            rules = owner.MemberDescriptor.GetCustomAttributes<MemberCollectionAttribute>(true).FirstOrDefault();
        if (value == null && rules?.NotNullItems == true) return false;
        var type = collection.Descriptor.GetInnerCollectionType();
        if (ContentAssetReference.Supports(type)) ContentAssetReference.Validate(type, value, rules?.NotNullItems != true);
        if (value != null && AuthoringGraph.Definition.IsTargetItemObjectReference(collection, index, value))
        {
            if (value is ISharedRenderer renderer) return CompositorAsset!.SharedRenderers.Any(item => ReferenceEquals(item, renderer));
            if (value is RenderStage stage) return CompositorAsset!.RenderStages.Any(item => ReferenceEquals(item, stage));
            if (value is SceneCameraSlot camera) return CompositorAsset!.Cameras.Any(item => ReferenceEquals(item, camera));
        }
        return true;
    }
    private void UpdateCompositorClipboardContext(IMemberNode member, object value)
    {
        // A null collection may just have been created by the native processor;
        // refresh canonical membership before it begins inserting actual items.
        RefreshAuthoringNodeAccess();
        if (member.Name != nameof(FXAAEffect.Dither) || member.Parent.Retrieve() is not FXAAEffect || value is not FXAAEffect.DitherType mode) return;
        var (minimum, maximum) = FXAAEffect.GetQualityRange(mode);
        var quality = member.Parent[nameof(FXAAEffect.Quality)]; var normalized = Math.Clamp((int)quality.Retrieve(), minimum, maximum);
        if (Equals(normalized, quality.Retrieve())) return;
        if (!CanEditAssetSettingNode(quality) || !CanAssignAuthoringNode(quality)) throw new InvalidOperationException("The FXAA quality required by this dither mode is read-only.");
        quality.Update(normalized);
    }
}
