using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorOutputEntry[] ReadVisualCaptureFailures()
    {
        long version = -1;
        outputLog.Read(ref version, out var entries);
        return entries.Where(entry => entry.Level is LogMessageType.Error or LogMessageType.Fatal).TakeLast(8).ToArray();
    }

    private object InspectVisualCaptureLoadState()
    {
        var state = CaptureInspectionSnapshot();
        return new { state.Project, state.Operations, state.Viewport, state.Documents,
            thumbnailDiagnostics = assetThumbnails?.InspectCaptureState(),
            errors = ReadVisualCaptureFailures().Select(entry => new
            { entry.Sequence, entry.Module, entry.Level, entry.Text, entry.ExceptionType }).ToArray() };
    }

    private (string? Id, string? Path, string? Url, bool Ready) InspectVisualCapturePreviewIdentity()
    {
        string? url = null; var ready = false; var isolatedMedia = false;
        if (modelPreview is { IsDisposed: false } model)
        {
            url = model.Name; ready = model.Visible && model.RenderCount > 0 && model.TargetWidth > 0 && model.RenderError == null;
        }
        else if (texturePreview is { IsDisposed: false } texture)
        {
            url = texture.Name; ready = texture.Visible && texture.RenderCount > 0 && texture.TargetWidth > 0 && texture.RenderError == null;
        }
        else if (materialPreview is { } material) { url = material.CaptureSourceUrl; ready = material.CaptureReady; }
        else if (skyboxPreview is { } skybox) { url = skybox.CaptureSourceUrl; ready = skybox.CaptureReady; }
        else if (fontPreview is { IsDisposed: false } font) { url = font.SourceUrl; ready = font.CaptureReady; }
        else if (spriteSheetPreview is { IsDisposed: false } sprites) { url = sprites.SourceUrl; ready = sprites.CaptureReady; }
        else if (animationPreview is { } animation) { url = animation.CaptureSourceUrl; ready = animation.CaptureReady; isolatedMedia = true; }
        else if (soundPreview is { } sound)
        {
            url = sound.SourceUrl; isolatedMedia = true;
            // Sound has an adopted engine instance and controls, rather than a GPU viewport.
            ready = sound.IsValid && IsVisualCapturePreviewBodyVisible();
        }
        else if (catalogPreviewPanel is { } catalog) { (url, ready) = catalog.CaptureOwner; }
        if (url == null || workspace == null) return (null, null, null, false);
        var matches = workspace.AssetItems.Where(item => NativeAssetUrls.Qualify(item) == url
            // Catalog, font and sprite renderers load from their retained content mount's
            // local URL. Resolve that actual renderer URL, never the requested selection.
            // The unique-owner check below rejects namespace collisions.
            || item.Location.ToString() == url
            || isolatedMedia && item.Asset is Stride.Assets.Models.AnimationAsset { Type: Stride.Assets.Models.DifferenceAnimationAssetType }
                && item.Location.ToString() + Stride.Assets.Models.AnimationAssetCompiler.SrcClipSuffix == url).ToArray();
        return matches.Length == 1 ? (matches[0].Id.ToString(), matches[0].FullPath.ToOSPath(), url, ready)
            : (null, null, url, false);
    }
    private static unsafe bool IsVisualCapturePreviewBodyVisible()
    {
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(PreviewWindowCaption.Text);
        return !window.IsNull && window.Active && !window.Hidden && !window.SkipItems
            && window.LastFrameActive == Hexa.NET.ImGui.ImGui.GetFrameCount()
            && (!window.DockIsActive || window.DockTabIsVisible);
    }
    private string? VisualCapturePreviewAssetId => InspectVisualCapturePreviewIdentity().Id;
    private string? VisualCapturePreviewAssetPath => InspectVisualCapturePreviewIdentity().Path;
    private string? VisualCapturePreviewUrl => InspectVisualCapturePreviewIdentity().Url;
    private bool VisualCapturePreviewReady => InspectVisualCapturePreviewIdentity().Ready;
}
