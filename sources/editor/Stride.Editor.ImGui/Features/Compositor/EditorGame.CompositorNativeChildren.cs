using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorNativeChildren(AssetMetadataDocument source, IReadOnlyList<INodePresenter> parents,
        bool additionalCollectionChildren = false)
    {
        if (!compositorEditors.TryGetValue(source, out var state) || parents.Count == 0) return;
        var properties = source.GetCompositorNativeChildProperties(parents);
        if (additionalCollectionChildren)
            properties = properties.Where(property => !property.First.Accessor.IsItem).ToArray();
        DrawNativeCompositorAggregateRows(source, state, properties, null, parents);
    }
    private void DrawCompositorPropertyChildren(AssetMetadataDocument source, CompositorProperty property)
    {
        if (property.NativePresenter is { } native) DrawCompositorNativeChildren(source, [native]);
        else DrawCompositorProperties(source, property.Details);
    }
    private void DrawCompositorAggregateChildren(AssetMetadataDocument source, CompositorEditorState state, CompositorAggregateProperty property)
    {
        if (property.Fields.All(field => field.NativePresenter != null))
            DrawCompositorNativeChildren(source, property.Fields.Select(field => field.NativePresenter!).ToArray());
        else DrawCompositorAggregatePropertyGroups(source, state, property.Details);
    }
    private static bool HasCompositorNativeChildren(CompositorProperty property)
        => property.NativePresenter?.Children.Count > 0 && !AssetMetadataDocument.IsCompositorValueType(property.Type)
            && !ContentAssetReference.Supports(property.Type);
    private static bool IsCompositorNativeComplex(VirtualNodePresenter presenter)
    {
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        return presenter.Children.Count != 0 && !AssetMetadataDocument.IsCompositorValueType(type)
            && !ContentAssetReference.Supports(type);
    }
    private static void SetCompositorVirtualExpansion(INodePresenter presenter)
    {
        var expansion = presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule) ? rule : Stride.Core.ExpandRule.Auto;
        Gui.SetNextItemOpen(expansion != Stride.Core.ExpandRule.Never, Hexa.NET.ImGui.ImGuiCond.Once);
    }
}
