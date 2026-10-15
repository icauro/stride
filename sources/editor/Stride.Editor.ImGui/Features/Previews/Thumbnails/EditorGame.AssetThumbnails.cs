using System.Text;
using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Assets.Effect;
using Stride.Assets.Sprite;
using Stride.Assets.SpriteFont;
using Stride.Assets.Models;
using Stride.UI.Assets;
using Stride.Assets.Entities;
using Stride.Engine;
using Stride.SpriteStudio.Assets;
using Stride.SpriteStudio.Runtime;
using Stride.Graphics.Font;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetThumbnailService? assetThumbnails;
    private ProjectWorkspace? thumbnailWorkspace;
    private IReadOnlyList<AssetItem>? thumbnailAssetItems;
    private long thumbnailSourceEpoch;
    private long thumbnailShaderEpoch = -1;
    private string thumbnailShaderIdentity = "";
    private readonly Dictionary<AssetId, (long Epoch, AssetThumbnailSource Source)> thumbnailSources = [];
    private readonly Dictionary<AssetId, (IAssetTagDocument Document, long Revision, long MaterialVersion, bool Dirty)> thumbnailAuthoredStates = [];
    private readonly List<AssetId> invalidThumbnailSources = [];
    private readonly List<AssetId> releasedThumbnailDocuments = [];
    private readonly HashSet<AssetId> seenThumbnailDocuments = [];
    private readonly Dictionary<AssetId, (Stride.Core.IO.UFile? Source, string Path)> thumbnailSourceLocations = [];
    private readonly Dictionary<AssetId, (string[] Sources, string Path)> thumbnailSpriteSources = [];
    private readonly Dictionary<AssetId, (string[] Sources, string Path)> thumbnailFontSources = [];
    private bool thumbnailWatchersNeedRefresh;
    private readonly Dictionary<AssetId, AssetThumbnailStatus> assetThumbnailBuildStatuses = [];
    private ProjectWorkspace? assetThumbnailStatusWorkspace;
    private void BeginAssetThumbnails()
    {
        if (renderer == null || IsEditorGraphicsRecovering || IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing) return;
        if (!ReferenceEquals(thumbnailWorkspace, workspace))
        {
            var statuses = ReferenceEquals(assetThumbnailStatusWorkspace, workspace) ? assetThumbnailBuildStatuses.ToArray() : [];
            ClearAssetThumbnails();
            foreach (var status in statuses) assetThumbnailBuildStatuses[status.Key] = status.Value;
            assetThumbnailStatusWorkspace = workspace;
        }
        if (workspace == null) return;
        if (assetThumbnails == null)
        {
            var owningWorkspace = workspace;
            assetThumbnails = new AssetThumbnailService(this, renderer!, workspace, ResolveAssetThumbnail, (asset, error) =>
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Thumbnail", asset.Name + ": " + error), InvalidateThumbnailSource,
                state => { if (ReferenceEquals(owningWorkspace, workspace)) RecordBuildOutput(state); });
            thumbnailAssetItems = workspace.AssetItems;
            foreach (var status in assetThumbnailBuildStatuses) assetThumbnails.SetBuildStatus(status.Key, status.Value);
        }
        if (!ReferenceEquals(thumbnailAssetItems, workspace.AssetItems))
        {
            thumbnailAssetItems = workspace.AssetItems; assetThumbnails.Invalidate();
            thumbnailSourceLocations.Clear();
            assetThumbnails.RefreshWatchedSources(workspace);
            SyncThumbnailAuthoredStates(true);
        }
        SyncThumbnailAuthoredStates();
        thumbnailWorkspace = workspace; assetThumbnails.BeginFrame(frames);
    }
    private AssetThumbnailSource ResolveAssetThumbnail(AssetEntry entry)
    {
        if (thumbnailSources.TryGetValue(entry.Id, out var cached) && cached.Epoch == thumbnailSourceEpoch) return cached.Source;
        var item = workspace!.FindAsset(entry.Id);
        var url = item.Location.ToString();
        var content = materialCompiledContent ?? projectContent; var asset = CurrentThumbnailAsset(item);
        if (assetThumbnails?.GetNativeCompiler(asset.GetType()) is { } native)
        {
            if (!native.IsStatic && content == null) throw new AssetThumbnailNotCompiledException(url);
            var capture = CaptureNativeThumbnail(entry, native.GetType());
            try
            {
                var nativeSettings = item.Package.GetGameSettingsAssetOrDefault();
                var nativeIds = capture.Authored.Assets.Select(input => new AssetId(Guid.Parse(input.Input.Id))).Where(id => id != item.Id).ToArray();
                return CacheThumbnailSource(entry.Id, new AssetThumbnailSource(content, asset, url, capture.Authored.Parameters.ColorSpace,
                    capture.Identity, capture.Identity, nativeSettings.GetOrCreate<EditorSettings>().RenderingMode, nativeIds,
                    capture.Identity, NativeCompilation: capture));
            }
            catch { capture.Dispose(); throw; }
        }
        var staticIcon = StaticAssetThumbnailIcons.ForAssetType(asset.GetType());
        if (asset is AnimationAsset animationWithoutModel && AnimationModelForThumbnail(item, animationWithoutModel) == null)
            staticIcon = "thumbnail-animation";
        if (staticIcon != null || !AssetThumbnailService.HasRenderedProvider(asset.GetType()))
        {
            // WPF's Asset fallback never preloads runtime content: source-only assets and
            // assets without a runtime URL still receive their actual type artwork.
            var gameSettings = item.Package.GetGameSettingsAssetOrDefault();
            var space = gameSettings.GetOrCreate<RenderingSettings>().ColorSpace;
            var typeIdentity = staticIcon != null ? StaticAssetThumbnail.Identity(staticIcon, space)
                : CustomAssetThumbnail.Identity(asset.GetType()) + "/" + space;
            var typeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeIdentity)));
            var typeDependencies = workspace.Session.DependencyManager.ComputeDependencies(item.Id,
                AssetDependencySearchOptions.Out | AssetDependencySearchOptions.Recursive, ContentLinkType.Reference);
            var typeDependencyIds = typeDependencies?.LinksOut.Select(link => link.Item.Id).Distinct().ToArray() ?? [];
            return CacheThumbnailSource(entry.Id, new AssetThumbnailSource(content, asset, url, space, typeIdentity,
                typeIdentity, gameSettings.GetOrCreate<EditorSettings>().RenderingMode, typeDependencyIds, typeKey, StaticIcon: staticIcon));
        }
        if (content == null) throw new AssetThumbnailNotCompiledException(url);
        if (asset is SpriteSheetAsset)
        {
            if (refreshedSpriteSheets.TryGetValue(url, out var refreshedSheet) && HasCurrentSpriteSheetContent(refreshedSheet)) content = refreshedSheet.Content.Bundle;
            if (documents.AuthoredDocuments.OfType<AssetMetadataDocument>().FirstOrDefault(document => document.AuthoredAsset.Id == item.Id) is { SpriteSettings: not null } authoredSheet)
                asset = authoredSheet.AuthoredAsset;
        }
        if (asset is SpriteFontAsset or PrecompiledSpriteFontAsset)
        {
            if (refreshedFonts.TryGetValue(url, out var installedFont) && HasCurrentFontContent(installedFont)) { content = installedFont.Content.Bundle; asset = installedFont.Snapshot; }
            else if (documents.AuthoredDocuments.OfType<AssetMetadataDocument>().FirstOrDefault(document => document.AuthoredAsset.Id == item.Id) is { IsFontAsset: true } authoredFont) asset = authoredFont.AuthoredAsset;
            asset = AssetCloner.Clone(asset); FontSourcePaths.Resolve(asset, item.FullPath.ToOSPath());
            if (asset is SpriteFontAsset sourceFont && (sourceFont.FontSource.GetFontPath() is not { } fontPath || !File.Exists(fontPath)))
                throw new AssetSourceUnavailableException("The asset source is unreachable.");
        }
        if (asset is TextureAsset texture)
        {
            var compiled = TexturePreviewSource(entry.Path, texture); asset = compiled.Asset;
            if (materialCompiledContent == null) content = compiled.Content;
        }
        else
        {
            var refreshed = refreshedModels.LastOrDefault(resource => resource.Url == url).Bundle;
            if (materialCompiledContent == null && refreshed != null) content = refreshed;
        }
        if (asset is IAssetWithSource sourceAsset)
        {
            if (string.IsNullOrEmpty(sourceAsset.Source)) throw new AssetSourceUnavailableException("The asset source is unreachable.");
            var sourcePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(item.FullPath.ToOSPath())!, sourceAsset.Source.ToOSPath()));
            if (!File.Exists(sourcePath)) throw new AssetSourceUnavailableException("The asset source is unreachable.", sourcePath);
        }
        if (asset is SpriteStudioModelAsset spriteStudio)
        {
            if (string.IsNullOrEmpty(spriteStudio.Source)) throw new AssetSourceUnavailableException("The asset source is unreachable.");
            var sourcePath = Path.GetFullPath(spriteStudio.Source.ToOSPath(), Path.GetDirectoryName(item.FullPath.ToOSPath())!);
            if (!File.Exists(sourcePath)) throw new AssetSourceUnavailableException("The asset source is unreachable.", sourcePath);
        }
        var index = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        if (!index.TryGetValue(url, out var id)) throw new AssetThumbnailNotCompiledException(url);
        var identity = new StringBuilder(url).Append(':').Append(id);
        string? previewModelUrl = null;
        AssetItem? previewModel = null;
        if (asset is AnimationAsset animation)
        {
            var model = previewModel = AnimationModelForThumbnail(item, animation);
            previewModelUrl = model?.Location.ToString();
            if (model != null)
            {
                identity.Append("/preview-model:").Append(model.Id).Append(':').Append(previewModelUrl);
                if (index.TryGetValue(previewModelUrl!, out var modelId)) identity.Append(':').Append(modelId);
            }
            if (animation.Type is DifferenceAnimationAssetType && index.TryGetValue(url + AnimationAssetCompiler.SrcClipSuffix, out var sourceClipId))
                identity.Append("/source-clip:").Append(sourceClipId);
        }
        var referenceType = asset switch
        {
            SpriteSheetAsset => typeof(SpriteSheet),
            SpriteFontAsset or PrecompiledSpriteFontAsset => typeof(SpriteFont),
            SpriteStudioModelAsset => typeof(SpriteStudioSheet),
            UIPageAsset => typeof(UIPage),
            PrefabAsset => typeof(Prefab),
            _ => null,
        };
        if (referenceType != null)
        {
            var referenced = CompiledThumbnailReferences.Read(content, url, referenceType);
            foreach (var textureUrl in referenced)
            {
                identity.Append('/').Append(textureUrl);
                if (index.TryGetValue(textureUrl, out var atlasId)) identity.Append(':').Append(atlasId);
                if (index.TryGetValue(textureUrl + "_Data", out var atlasDataId)) identity.Append(':').Append(atlasDataId);
            }
            if (asset is SpriteFontAsset { FontType: RuntimeRasterizedSpriteFontType } font)
            {
                var fontUrl = FontHelper.GetFontPath(font.FontSource.GetFontName(), font.FontSource.Style);
                identity.Append('/').Append(fontUrl); if (index.TryGetValue(fontUrl, out var fontId)) identity.Append(':').Append(fontId);
            }
        }
        if (index.TryGetValue(url + "_Data", out var dataId)) identity.Append('/').Append(dataId);
        var dependencies = workspace.Session.DependencyManager.ComputeDependencies(item.Id,
            AssetDependencySearchOptions.Out | AssetDependencySearchOptions.Recursive, ContentLinkType.Reference);
        var dependencyIds = new List<AssetId>();
        var dependencyItems = new Dictionary<AssetId, AssetItem>();
        if (dependencies != null)
            foreach (var dependency in dependencies.LinksOut) dependencyItems[dependency.Item.Id] = dependency.Item;
        if (previewModel != null)
        {
            dependencyItems[previewModel.Id] = previewModel;
            var modelDependencies = workspace.Session.DependencyManager.ComputeDependencies(previewModel.Id,
                AssetDependencySearchOptions.Out | AssetDependencySearchOptions.Recursive, ContentLinkType.Reference);
            if (modelDependencies != null)
                foreach (var dependency in modelDependencies.LinksOut) dependencyItems[dependency.Item.Id] = dependency.Item;
        }
        foreach (var dependency in dependencyItems.Values.OrderBy(dependency => dependency.Id.ToString(), StringComparer.Ordinal))
            {
                var dependencyUrl = dependency.Location.ToString();
                dependencyIds.Add(dependency.Id);
                identity.Append('/').Append(dependency.Id);
                if (index.TryGetValue(dependencyUrl, out var dependencyId)) identity.Append(':').Append(dependencyId);
                if (index.TryGetValue(dependencyUrl + "_Data", out var dependencyDataId)) identity.Append(':').Append(dependencyDataId);
                var dependencyAsset = CurrentThumbnailAsset(dependency);
                var dependencyReferenceType = dependencyAsset switch
                {
                    SpriteSheetAsset => typeof(SpriteSheet),
                    SpriteFontAsset or PrecompiledSpriteFontAsset => typeof(SpriteFont),
                    SpriteStudioModelAsset => typeof(SpriteStudioSheet),
                    _ => null,
                };
                if (dependencyReferenceType != null && index.Contains(dependencyUrl))
                    foreach (var reference in CompiledThumbnailReferences.Read(content, dependencyUrl, dependencyReferenceType))
                    {
                        identity.Append('/').Append(reference);
                        if (index.TryGetValue(reference, out var referencedId)) identity.Append(':').Append(referencedId);
                        if (index.TryGetValue(reference + "_Data", out var referencedDataId)) identity.Append(':').Append(referencedDataId);
                    }
                if (dependencyAsset is SpriteFontAsset { FontType: RuntimeRasterizedSpriteFontType } dependencyFont)
                {
                    var fontUrl = FontHelper.GetFontPath(dependencyFont.FontSource.GetFontName(), dependencyFont.FontSource.Style);
                    identity.Append('/').Append(fontUrl); if (index.TryGetValue(fontUrl, out var fontId)) identity.Append(':').Append(fontId);
                }
            }
        if (asset is not TextureAsset and not SpriteSheetAsset and not SpriteFontAsset and not PrecompiledSpriteFontAsset)
        {
            if (thumbnailShaderEpoch != thumbnailSourceEpoch)
            {
                var shaders = new StringBuilder();
                foreach (var shader in workspace.AssetItems.Where(candidate => candidate.Asset is EffectShaderAsset)
                             .OrderBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal))
                {
                    // Match EffectShaderAssetCompiler's imported shader-content URL.
                    var shaderUrl = EffectCompilerBase.DefaultSourceShaderFolder + "/" + Path.GetFileName(shader.FullPath.ToOSPath());
                    shaders.Append('/').Append(shader.Id);
                    if (index.TryGetValue(shaderUrl, out var shaderId)) shaders.Append(':').Append(shaderId);
                }
                thumbnailShaderIdentity = shaders.ToString(); thumbnailShaderEpoch = thumbnailSourceEpoch;
            }
            identity.Append(thumbnailShaderIdentity);
        }
        var settings = item.Package.GetGameSettingsAssetOrDefault();
        var rendering = settings.GetOrCreate<RenderingSettings>();
        var mode = settings.GetOrCreate<EditorSettings>().RenderingMode;
        var compiledIdentity = identity.ToString();
        var settingsIdentity = rendering.ColorSpace + "/" + rendering.DefaultGraphicsProfile + "/" + mode;
        var hint = asset is TextureAsset actualTexture ? actualTexture.Type.Hint.ToString()
            : asset is SpriteFontAsset or PrecompiledSpriteFontAsset ? SpriteFontAssetThumbnail.Title(asset)
            : asset is UIPageAsset uiPage ? "UI/" + uiPage.Design.Resolution : "Entity";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("AssetThumbnail/v2/128/" + entry.AssetType.FullName + "/" + compiledIdentity
            + "/" + settingsIdentity + "/" + rendering.ColorSpace + "/" + hint)));
        var source = new AssetThumbnailSource(content, AssetCloner.Clone(asset), url, rendering.ColorSpace, compiledIdentity, settingsIdentity, mode, dependencyIds.ToArray(), key, previewModelUrl);
        return CacheThumbnailSource(entry.Id, source);
    }
    private Asset CurrentThumbnailAsset(AssetItem item) => thumbnailAuthoredStates.TryGetValue(item.Id, out var retained)
        ? retained.Document.AuthoredAsset : item.Asset;
    private AssetItem? AnimationModelForThumbnail(AssetItem item, AnimationAsset animation)
    {
        var model = item.Package.FindAssetFromProxyObject(animation.PreviewModel);
        return model != null && CurrentThumbnailAsset(model) is ModelAsset { Skeleton: not null } ? model : null;
    }
    private AssetThumbnailSource CacheThumbnailSource(AssetId id, AssetThumbnailSource source)
    {
        source = source with { TypeScope = PackageTypeScope.For(workspace!.FindAsset(id).Package) };
        if (!thumbnailSources.ContainsKey(id) && thumbnailSources.Count >= 2048)
        {
            AssetId oldest = default;
            foreach (var idToRemove in thumbnailSources.Keys) { oldest = idToRemove; break; }
            ReleaseThumbnailSource(oldest);
        }
        if (thumbnailSources.TryGetValue(id, out var previous) && !ReferenceEquals(previous.Source, source)) ReleaseThumbnailSource(id);
        thumbnailSources[id] = (thumbnailSourceEpoch, source); return source;
    }
    private void ReleaseThumbnailSource(AssetId id)
    {
        if (!thumbnailSources.TryGetValue(id, out var source)) return;
        source.Source.NativeCompilation?.Dispose(); thumbnailSources.Remove(id);
    }
    private void ReleaseAllThumbnailSources()
    {
        var failures = new List<Exception>();
        foreach (var id in thumbnailSources.Keys.ToArray())
            try { ReleaseThumbnailSource(id); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Native thumbnail source cleanup failed.", failures);
    }
    private void SyncThumbnailAuthoredStates(bool compiled = false, AssetId? compiledAuthoredTexture = null)
    {
        seenThumbnailDocuments.Clear();
        var sourceLocationsChanged = thumbnailWatchersNeedRefresh; thumbnailWatchersNeedRefresh = false;
        foreach (var owner in documents.AuthoredDocuments)
        {
            if (owner is not IAssetTagDocument document) continue;
            var materialVersion = document is AssetMetadataDocument metadata ? metadata.MaterialSettingsEditVersion : 0;
            var settingsRevision = document is AssetMetadataDocument settings
                ? settings.IsFontAsset ? settings.FontSettingsEditVersion : settings.SpriteSettings != null ? settings.SpriteSettingsEditVersion
                    : settings.TextureSettings != null ? settings.TextureSettingsEditVersion : document.EditRevision : document.EditRevision;
            var state = (Document: document, Revision: settingsRevision, MaterialVersion: materialVersion, Dirty: document.IsDirty);
            seenThumbnailDocuments.Add(document.AuthoredAsset.Id);
            if (document is AssetMetadataDocument { IsFontAsset: true } fontDocument)
            {
                var watch = WatchFontSources(fontDocument); var location = (watch.Sources, watch.Path);
                if (!thumbnailFontSources.TryGetValue(document.AuthoredAsset.Id, out var previous)
                    || !ReferenceEquals(previous.Sources, location.Sources) || previous.Path != location.Path)
                { thumbnailFontSources[document.AuthoredAsset.Id] = location; sourceLocationsChanged = true; }
            }
            if (document is AssetMetadataDocument { SpriteSettings: not null } spriteDocument)
            {
                var watch = WatchSpriteSources(spriteDocument); var location = (watch.Sources, watch.Path);
                if (!thumbnailSpriteSources.TryGetValue(document.AuthoredAsset.Id, out var previous)
                    || !ReferenceEquals(previous.Sources, location.Sources) || previous.Path != location.Path)
                { thumbnailSpriteSources[document.AuthoredAsset.Id] = location; sourceLocationsChanged = true; }
            }
            if (document.AuthoredAsset is IAssetWithSource or SpriteStudioModelAsset)
            {
                var location = (Source: document.AuthoredAsset.MainSource, document.Path);
                if (!thumbnailSourceLocations.TryGetValue(document.AuthoredAsset.Id, out var beforeLocation)
                    || !Equals(beforeLocation.Source, location.Source) || beforeLocation.Path != location.Path)
                { thumbnailSourceLocations[document.AuthoredAsset.Id] = location; sourceLocationsChanged = true; }
            }
            if (compiled)
            {
                thumbnailAuthoredStates[document.AuthoredAsset.Id] = state;
                if (document.IsDirty && document.AuthoredAsset.Id != compiledAuthoredTexture && !HasCurrentCompiledThumbnail(document)) assetThumbnails!.Invalidate(document.AuthoredAsset.Id, true);
                continue;
            }
            if (!thumbnailAuthoredStates.TryGetValue(document.AuthoredAsset.Id, out var before))
            { thumbnailAuthoredStates[document.AuthoredAsset.Id] = state;
                sourceLocationsChanged |= assetThumbnails!.GetNativeCompiler(document.AuthoredAsset.GetType()) != null;
                if (document.IsDirty) assetThumbnails.Invalidate(document.AuthoredAsset.Id, !HasCurrentCompiledThumbnail(document)); }
            else if (!ReferenceEquals(before.Document, document) || before.Revision != state.Revision
                || before.MaterialVersion != materialVersion || before.Dirty != document.IsDirty)
            { thumbnailAuthoredStates[document.AuthoredAsset.Id] = state;
                sourceLocationsChanged |= assetThumbnails!.GetNativeCompiler(document.AuthoredAsset.GetType()) != null;
                assetThumbnails.Invalidate(document.AuthoredAsset.Id, !HasCurrentCompiledThumbnail(document)); }
        }
        releasedThumbnailDocuments.Clear();
        foreach (var pair in thumbnailAuthoredStates)
            if (!seenThumbnailDocuments.Contains(pair.Key)) releasedThumbnailDocuments.Add(pair.Key);
        foreach (var id in releasedThumbnailDocuments)
        {
            thumbnailAuthoredStates.Remove(id); assetThumbnails!.Invalidate(id);
            sourceLocationsChanged |= thumbnailSourceLocations.Remove(id);
            sourceLocationsChanged |= thumbnailSpriteSources.Remove(id);
            sourceLocationsChanged |= thumbnailFontSources.Remove(id);
        }
        if (sourceLocationsChanged)
        {
            var retainedSources = new Dictionary<AssetId, (Asset Asset, string Path)>();
            foreach (var owner in documents.AuthoredDocuments)
                if (owner is IAssetTagDocument document)
                    retainedSources[document.AuthoredAsset.Id] = (document.AuthoredAsset, document.Path);
            assetThumbnails!.RefreshWatchedSources(workspace!, retainedSources);
        }
    }
    private bool HasCurrentCompiledThumbnail(IAssetTagDocument document)
    {
        if (document is AssetMetadataDocument { IsFontAsset: true } font && fontSourceWatches.TryGetValue(font, out var fontWatch))
        {
            var revision = CurrentFontRevision(font);
            if (font.FontSettingsEditVersion == 0 && fontWatch.Source == 0 && !refreshedFonts.ContainsKey(fontWatch.Url)) return Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(fontWatch.Url);
            return compiledFontRevisions.TryGetValue(font, out var compiled) && compiled == revision
                && refreshedFonts.TryGetValue(fontWatch.Url, out var installed) && HasCurrentFontContent(installed);
        }
        if (document is AssetMetadataDocument { SpriteSettings: not null } sprite && spriteSourceWatches.TryGetValue(sprite, out var watch))
        {
            var revision = SpriteRevision(sprite);
            if (revision == default && !refreshedSpriteSheets.ContainsKey(watch.Url)) return Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(watch.Url);
            return compiledSpriteRevisions.TryGetValue(sprite, out var compiled) && compiled == revision
                && refreshedSpriteSheets.TryGetValue(watch.Url, out var installed) && HasCurrentSpriteSheetContent(installed);
        }
        return HasCurrentCompiledTexture(document);
    }
    private void InvalidateThumbnailSource(AssetId? id)
    {
        if (id == null) { thumbnailSourceEpoch++; ReleaseAllThumbnailSources(); return; }
        invalidThumbnailSources.Clear();
        foreach (var pair in thumbnailSources)
            if (pair.Key == id || pair.Value.Source.DependencyIds.Contains(id.Value)) invalidThumbnailSources.Add(pair.Key);
        foreach (var key in invalidThumbnailSources) ReleaseThumbnailSource(key);
    }
    private void InvalidateCompiledAssetThumbnails(AssetId? compiledAuthoredTexture = null)
    {
        if (assetThumbnails == null) return;
        // Earlier source-file events must be consumed before the successful build
        // becomes authoritative. Dirty retained documents still await their own build.
        assetThumbnails.BeginFrame(frames);
        assetThumbnails.Invalidate();
        thumbnailWatchersNeedRefresh = true; // Imports can change the SpriteStudio cell-map/image closure.
        SyncThumbnailAuthoredStates(true, compiledAuthoredTexture);
    }
    private void ReleaseThumbnailDocument(IEditorDocument document)
    {
        if (document is not IAssetTagDocument assetDocument) return;
        var id = assetDocument.AuthoredAsset.Id;
        if (thumbnailAuthoredStates.TryGetValue(id, out var state) && ReferenceEquals(state.Document, document))
        {
            thumbnailAuthoredStates.Remove(id);
            thumbnailWatchersNeedRefresh |= thumbnailSourceLocations.Remove(id);
            thumbnailWatchersNeedRefresh |= thumbnailSpriteSources.Remove(id);
            thumbnailWatchersNeedRefresh |= thumbnailFontSources.Remove(id);
            assetThumbnails?.Invalidate(id);
        }
    }
    private void DrawAssetThumbnail(AssetEntry asset, NVector2 position, float size)
    {
        var service = assetThumbnails;
        var reference = default(ImTextureRef);
        var available = false;
        if (service != null) available = service.RequestVisible(asset, out reference);
        var status = service?.GetStatus(asset.Id) ?? assetThumbnailBuildStatuses.GetValueOrDefault(asset.Id, AssetThumbnailStatus.Clear);
        var drawList = Gui.GetWindowDrawList();
        if (status.Failure is AssetThumbnailFailure.Broken or AssetThumbnailFailure.MissingSource)
            drawList.AddImage(icons!.Get(status.Failure == AssetThumbnailFailure.MissingSource ? "thumbnail-no-source" : "thumbnail-broken"), position, position + new NVector2(size));
        else if (available) drawList.AddImage(reference, position, position + new NVector2(size));
        if (status.Level >= Stride.Core.Diagnostics.LogMessageType.Warning)
        {
            // WPF's 32px status artwork has a 4px inset on a 128px thumbnail.
            var corner = size * 0.25f; var inset = size / 32;
            var maximum = position + new NVector2(size - inset);
            drawList.AddImage(icons!.Get(status.Level >= Stride.Core.Diagnostics.LogMessageType.Error ? "thumbnail-error" : "thumbnail-warning"),
                maximum - new NVector2(corner), maximum);
        }
    }
    private void RenderAssetThumbnails() => assetThumbnails?.RenderPending(frames);
    private void InvalidateAssetThumbnails(AssetId? id = null) => assetThumbnails?.Invalidate(id);
    private void SetAssetThumbnailBuildStatus(AssetId id, AssetThumbnailStatus status)
    {
        if (!ReferenceEquals(assetThumbnailStatusWorkspace, workspace)) assetThumbnailBuildStatuses.Clear();
        assetThumbnailStatusWorkspace = workspace;
        if (status == AssetThumbnailStatus.Clear) assetThumbnailBuildStatuses.Remove(id);
        else assetThumbnailBuildStatuses[id] = status;
        assetThumbnails?.SetBuildStatus(id, status);
    }
    private void ClearAssetThumbnails()
    {
        if (assetThumbnails is { } service)
        {
            if (!retiredAssetThumbnailServices.Any(owner => ReferenceEquals(owner.Service, service)))
                retiredAssetThumbnailServices.Add(new RetiredAssetThumbnailService(service));
            assetThumbnails = null;
            service.BeginShutdown();
        }
        thumbnailWorkspace = null;
        thumbnailAssetItems = null; ReleaseAllThumbnailSources(); thumbnailAuthoredStates.Clear(); invalidThumbnailSources.Clear(); thumbnailSourceEpoch++;
        thumbnailShaderEpoch = -1; thumbnailShaderIdentity = "";
        releasedThumbnailDocuments.Clear(); seenThumbnailDocuments.Clear();
        thumbnailSourceLocations.Clear();
        thumbnailSpriteSources.Clear();
        thumbnailFontSources.Clear();
        thumbnailWatchersNeedRefresh = false;
        assetThumbnailBuildStatuses.Clear(); assetThumbnailStatusWorkspace = null;
    }
    private sealed class RetiredAssetThumbnailService(AssetThumbnailService service)
    {
        internal readonly AssetThumbnailService Service = service;
        internal long RetryAt;
        internal string? Failure;
    }
    private readonly List<RetiredAssetThumbnailService> retiredAssetThumbnailServices = [];
    private bool IsAssetThumbnailServiceDraining => retiredAssetThumbnailServices.Count != 0;
    private bool RetiredThumbnailServicesUseContent(CompiledProjectContent content)
    {
        foreach (var owner in retiredAssetThumbnailServices)
            if (owner.Service.UsesContent(content)) return true;
        return false;
    }
    private void BeginAssetThumbnailServiceDrain()
    {
        ClearAssetThumbnails();
        foreach (var owner in retiredAssetThumbnailServices) owner.Service.BeginShutdown();
    }
    private void PumpRetiredAssetThumbnailServices()
    {
        foreach (var owner in retiredAssetThumbnailServices.ToArray())
        {
            if (Environment.TickCount64 < owner.RetryAt) continue;
            try
            {
                if (!owner.Service.PumpShutdown()) continue;
                if (owner.Service.ShutdownDiagnostic is { } diagnostic)
                    outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Thumbnail shutdown", diagnostic.ToString());
                retiredAssetThumbnailServices.Remove(owner);
            }
            catch (Exception error)
            {
                owner.RetryAt = Environment.TickCount64 + 250;
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                if (owner.Failure != error.ToString())
                { owner.Failure = error.ToString(); outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Thumbnail shutdown", owner.Failure); }
            }
        }
    }
}
