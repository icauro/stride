using Stride.Core.Assets;
using Stride.Core.Quantum;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private EditorAssetPropertyPresentation? extensionAssetPresentation;
    private long extensionAssetVersion = -1;
    private bool refreshingExtensionPresentation;
    internal bool HasExtensionAssetSettings => SessionHistory?.Extensions?.HasProviders == true
        && this is not UIDocument && this is not VisualScriptDocument
        && AuthoredAsset is not Stride.Assets.Textures.TextureAsset and not Stride.Assets.Materials.MaterialAsset
            and not Stride.Assets.GameSettingsAsset and not Stride.Assets.Sprite.SpriteSheetAsset
            and not Stride.Assets.Skyboxes.SkyboxAsset and not Stride.Assets.SpriteFont.SpriteFontAsset
            and not Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset
            and not Stride.Assets.Rendering.GraphicsCompositorAsset;
    private EditorAssetPropertyPresentation? ExtensionAssetPresentation
    {
        get
        {
            var extensions = SessionHistory?.Extensions;
            if (refreshingExtensionPresentation) return extensionAssetPresentation;
            // A live native command/gesture still owns this actual metadata generation.
            if (HasActiveNativeMutationJournal && extensionAssetPresentation != null) return extensionAssetPresentation;
            if (extensions == null || !extensions.HasProviders && !HasImportSettings && AuthoredAsset is not Stride.Assets.Materials.MaterialAsset and not Stride.Assets.Skyboxes.SkyboxAsset and not Stride.Assets.GameSettingsAsset and not Stride.Assets.SpriteFont.SpriteFontAsset and not Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset and not Stride.Assets.Textures.TextureAsset and not Stride.Assets.Sprite.SpriteSheetAsset and not Stride.Assets.Rendering.GraphicsCompositorAsset) { DisposeExtensionAssetPresentation(); return null; }
            if (extensionAssetVersion != extensions.Version)
            {
                refreshingExtensionPresentation = true;
                try
                {
                    EndExtensionVirtualEdit(); EndImportSettingEdit(); DisposeExtensionAssetPresentation();
                    extensionAssetPresentation = new(AuthoringGraph, extensions, shaderNames: () => MaterialShaderSources?.Invoke().Keys ?? Enumerable.Empty<string>()); extensionAssetVersion = extensions.Version;
                }
                finally { refreshingExtensionPresentation = false; }
            }
            return extensionAssetPresentation;
        }
    }
    internal virtual bool IsExtensionPropertyVisible(IMemberNode node) => ResolveExtensionPresentation(node)?.IsVisible(node) != false;
    internal virtual bool IsExtensionPropertyReadOnly(IMemberNode node) => ResolveExtensionPresentation(node)?.IsReadOnly(node) == true;
    internal virtual string? ExtensionPropertyLabel(IMemberNode node) => ResolveExtensionPresentation(node)?.Label(node);
    internal virtual EditorPresenterTarget? AcquireExtensionCommandTarget(IMemberNode node) => ResolveExtensionPresentation(node)?.AcquireCommandTarget(node);
    internal virtual IReadOnlyList<VirtualNodePresenter> ExtensionVirtualProperties => ActiveExtensionVirtualRows();
    internal virtual EditorPresenterTarget AcquireExtensionVirtualTarget(VirtualNodePresenter presenter)
        => ResolveExtensionPresentation(presenter)?.AcquireVirtualTarget(presenter) ?? throw new InvalidOperationException("The virtual property has no canonical presentation owner.");
    internal virtual bool CanEditExtensionVirtualProperty(VirtualNodePresenter presenter)
    {
        if (CompositorAsset != null && !ExtensionVirtualProperties.Contains(presenter)) return false;
        if (SpriteSettings != null && SelectedSpriteIds.Count != 0 && !IsCurrentSpriteVirtualGroup([presenter])) return false;
        var accessor = presenter.GetNodeAccessor();
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        return !presenter.IsReadOnly && (accessor.Node is IMemberNode member ? CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member)
            : accessor.Node is IAssetObjectNode collection && CanEditAssetSettingItem(collection, accessor.Index));
    }
    internal void UpdateExtensionVirtualProperty(VirtualNodePresenter presenter, object? value)
    {
        value = NormalizeExtensionVirtualValue(presenter, value);
        ApplyInheritedAssetMutation(() => presenter.UpdateValue(value!), "Edit " + presenter.DisplayName);
    }
    private object? NormalizeExtensionVirtualValue(VirtualNodePresenter presenter, object? value)
    {
        if (!CanEditExtensionVirtualProperty(presenter)) throw new InvalidOperationException("The virtual property's authored accessor is read-only or sealed.");
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (value != null && !type.IsInstanceOfType(value)) throw new ArgumentException("The virtual editor value has an incompatible type.");
        NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
        value = PropertyValueValidation.Normalize(value, ExtensionNumericRange(presenter));
        var names = NativeNumericAxes.Names(presenter.Type);
        value = NativeNumericAxes.Normalize(value, presenter.Value, names.Select(name =>
        {
            var child = presenter.TryGetChild(name);
            return child is VirtualNodePresenter virtualChild ? ExtensionNumericRange(virtualChild)
                : child?.GetNodeAccessor().Node is IMemberNode member ? ExtensionNumericRange(member) : null;
        }).ToArray());
        return value;
    }
    internal virtual void DisposeExtensionAssetPresentation()
    {
        ReleaseVirtualTargets();
        DisposeCompositorPresentations();
        DisposeSpriteFramePresentations();
        extensionAssetPresentation?.Dispose(); extensionAssetPresentation = null; extensionAssetVersion = -1;
    }
}
