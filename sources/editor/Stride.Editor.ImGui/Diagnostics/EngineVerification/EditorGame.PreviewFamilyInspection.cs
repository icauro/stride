using System.Reflection;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal AnimationAssetPreview? VerificationAnimation => animationPreview;
    internal PreviewToneMapVerification ConfigureAnimationVerificationExposure()
        => animationPreview != null && ReadFamilyField(animationPreview, "view") is ModelAssetPreview model
            ? model.ConfigureVerificationExposure()
            : throw new InvalidOperationException("The native animation model/compositor owner is unavailable.");
    internal int VerificationAnimationRenderCount
        => animationPreview != null && ReadFamilyField(animationPreview, "view") is ModelAssetPreview model ? model.RenderCount : 0;
    internal SpriteSheetAssetPreview? VerificationSprite => spriteSheetPreview;
    internal SpriteFontAssetPreview? VerificationFont => fontPreview;
    internal bool VerificationFamilyBrowserRecursive => assetBrowser.DisplayMode == AssetBrowserDisplayMode.RecursiveAssets;
    internal string? VerificationFamilyAssetControl(Stride.Core.Assets.AssetId id)
        => workspace?.Assets.FirstOrDefault(asset => asset.Id == id) is { } entry ? "asset/" + entry.Name : null;
    internal object CaptureFamilyAssetIdentity(Stride.Core.Assets.AssetId id)
    {
        var entry = workspace?.Assets.FirstOrDefault(asset => asset.Id == id);
        var item = workspace?.AssetItems.FirstOrDefault(asset => asset.Id == id);
        return new { id = id.ToString(), found = entry != null, nativeItemFound = item != null,
            name = entry?.Name, path = entry?.Path, nativeType = item?.Asset.GetType().AssemblyQualifiedName,
            location = item?.Location.ToString(), package = item?.Package?.FullPath?.ToString(),
            actualBrowserVisible = assetBrowser.Visible.Any(asset => asset.Id == id),
            displayMode = assetBrowser.DisplayMode.ToString(), selectedFolder = assetBrowser.SelectedFolder?.Id,
            compiled = workspace?.HasCompiledContent, compileFailure = workspace?.CompilationFailure?.ToString() };
    }
    internal Stride.Core.Mathematics.Vector2 VerificationFontGlyphMetrics(string sample)
        => ((CompiledFontContent?)ReadFamilyField(fontPreview ?? throw new InvalidOperationException("Missing font preview."), "content"))?.Font.MeasureString(sample)
            ?? throw new InvalidOperationException("Missing actual compiled font generation.");
    internal object? VerificationFamilyOwner(string family) => family switch
    { "animation" => animationPreview, "sprite" => spriteSheetPreview, "font" => fontPreview, _ => null };
    internal bool VerificationFamilyRendered(string family)
    {
        var owner = VerificationFamilyOwner(family);
        if (owner == null || renderer == null || !renderer.HasVerificationTexture(VerificationFamilyReference(family))) return false;
        if (family == "animation") return ReadFamilyField(owner, "view") is ModelAssetPreview { RenderCount: > 0 };
        return (bool?)ReadFamilyField(owner, "dirty") == false;
    }
    private ImTextureRef VerificationFamilyReference(string family) => family switch
    {
        "animation" => animationPreview?.TextureRef ?? throw new InvalidOperationException("Missing native animation preview."),
        "sprite" => spriteSheetPreview?.TextureRef ?? throw new InvalidOperationException("Missing native sprite preview."),
        "font" => fontPreview?.TextureRef ?? throw new InvalidOperationException("Missing native font preview."),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
    internal EngineOutputSample ReadFamilyOutput(string family)
        => EngineOutputSample.Read(GraphicsContext.CommandList, renderer!.RequireVerificationTexture(VerificationFamilyReference(family)));
    internal void SaveFamilyOutput(string family, string path)
    {
        using var file = File.Create(path);
        renderer!.RequireVerificationTexture(VerificationFamilyReference(family)).Save(GraphicsContext.CommandList, file, ImageFileType.Png);
    }
    internal PreviewFamilyLifetimeVerification CaptureFamilyLifetime(string family)
    {
        var owner = VerificationFamilyOwner(family) ?? throw new InvalidOperationException("Missing native preview owner.");
        var targetOwner = family == "animation" ? ReadFamilyField(owner, "view")! : owner;
        var targets = (PreviewTargetResources?)ReadFamilyField(targetOwner, "targetResources")
            ?? throw new InvalidOperationException("Native preview target owner is unavailable.");
        var cache = ReadFamilyField(owner, "cache") as ContentManager;
        var roots = cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
        if (family != "font" && roots.Length == 0) throw new InvalidOperationException("The native preview cache has no real compiled roots.");
        return new(owner, targets, VerificationFamilyReference(family), cache, roots);
    }
    internal bool FamilyLifetimeSettled(PreviewFamilyLifetimeVerification proof)
    {
        var disposed = proof.Owner switch
        {
            SpriteSheetAssetPreview sprite => sprite.IsDisposed,
            SpriteFontAssetPreview font => font.IsDisposed && font.IsRetirementDrainComplete
                && ReadFamilyField(font, "content") is CompiledFontContent { HasGraphicsResources: false, ActiveFont: null },
            AnimationAssetPreview animation => (bool?)ReadFamilyField(animation, "disposed") == true,
            _ => false,
        };
        var loaded = proof.Cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
        return disposed && proof.Targets.IsDisposed && !renderer!.HasVerificationTexture(proof.Reference)
            && !IsVerificationRetirementOwnerQueued(proof.Targets)
            && proof.Roots.All(root => !loaded.Any(item => ReferenceEquals(item, root)));
    }
    private static object? ReadFamilyField(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
}

internal sealed record PreviewFamilyLifetimeVerification(object Owner, PreviewTargetResources Targets, ImTextureRef Reference,
    ContentManager? Cache, object[] Roots);
