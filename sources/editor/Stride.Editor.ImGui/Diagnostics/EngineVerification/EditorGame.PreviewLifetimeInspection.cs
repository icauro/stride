using System.Reflection;
using Hexa.NET.ImGui;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed class PreviewLifetimeVerificationOwner(object preview, PreviewTargetResources targets,
    ContentManager cache, object contentRoot, ImTextureRef reference)
{
    internal object Preview => preview;
    internal PreviewTargetResources Targets => targets;
    internal ImTextureRef Reference => reference;
    internal bool PreviewDisposed => preview switch
    { TextureAssetPreview texture => texture.IsDisposed, ModelAssetPreview model => model.IsDisposed, _ => false };
    internal bool NativeCacheRootReleased => !cache.GetLoadedAssetsSnapshot().Any(asset => ReferenceEquals(asset, contentRoot));
    internal object Snapshot(EditorGame game) => new
    {
        previewType = preview.GetType().FullName, PreviewDisposed, targetDisposed = targets.IsDisposed,
        targetPresent = targets.Target != null, depthPresent = targets.Depth != null, presentationPresent = targets.Presentation != null,
        NativeCacheRootReleased, queuedTargetOwner = game.IsVerificationRetirementOwnerQueued(targets),
        registrationPresent = game.IsVerificationPreviewRegistered(reference),
    };
    internal bool IsSettled(EditorGame game) => PreviewDisposed && targets.IsDisposed && NativeCacheRootReleased
        && !game.IsVerificationRetirementOwnerQueued(targets) && !game.IsVerificationPreviewRegistered(reference);
}

internal sealed partial class EditorGame
{
    internal PreviewLifetimeVerificationOwner CapturePreviewLifetimeVerification(bool model)
    {
        object preview = (model ? (object?)modelPreview : texturePreview)
            ?? throw new InvalidOperationException("E06: The actual preview owner is unavailable.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = preview.GetType();
        // Read actual ownership fields; no caches or substitute target generations are created.
        var targets = type.GetField("targetResources", flags)!.GetValue(preview) as PreviewTargetResources
            ?? throw new InvalidOperationException("E06: The preview has no actual GPU target owner.");
        var cache = type.GetField("content", flags)!.GetValue(preview) as ContentManager
            ?? throw new InvalidOperationException("E06: The fixture preview is not using a compiled native object cache.");
        var root = type.GetField(model ? "model" : "texture", flags)!.GetValue(preview)
            ?? throw new InvalidOperationException("E06: The compiled root resource is unavailable.");
        if (!cache.GetLoadedAssetsSnapshot().Any(asset => ReferenceEquals(asset, root)))
            throw new InvalidOperationException("E06: The preview's real content cache does not own the compiled root resource.");
        return new(preview, targets, cache, root, targets.Reference);
    }
    internal bool IsVerificationRetirementOwnerQueued(IDisposable owner) => retiredEditorPreviewOwners.Contains(owner);
    internal bool IsVerificationPreviewRegistered(ImTextureRef reference) => renderer?.HasVerificationTexture(reference) == true;
    internal int VerificationPendingPreviewOwners => retiredEditorPreviews.Count;
}

internal sealed unsafe partial class ImGuiRenderer
{
    internal bool HasVerificationTexture(ImTextureRef reference) => textures.ContainsKey(reference.GetTexID().Handle);
}
