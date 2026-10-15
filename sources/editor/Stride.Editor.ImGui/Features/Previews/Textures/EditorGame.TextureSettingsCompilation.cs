using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class RetiredSceneTexture(EditorGame host, ContentManager cache, Texture texture) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            if (released) return;
            host.ReleaseSceneTextureContent(cache, texture); released = true;
        }
    }
    private readonly Dictionary<AssetMetadataDocument, TextureSettingsRevision> compiledTextureSettingRevisions = [];
    private readonly record struct InstalledTextureRevision(TextureSettingsRevision Revision, string Url, ObjectId Root, ObjectId Data);
    private readonly Dictionary<AssetMetadataDocument, InstalledTextureRevision> installedTextureSettingRevisions = [];
    private readonly Dictionary<string, (CompiledProjectContent Content, TextureAsset Asset)> refreshedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(CompiledProjectContent Content, int Frame)> retiredTextureContent = [];
    private ProjectWorkspace? textureSettingsWorkspace;
    private Task<string>? textureSettingsBuild;
    private CancellationTokenSource? textureSettingsCancellation;
    private ProjectLoadState? textureSettingsBuildState;
    private int textureSettingsBuildLevel;
    private AssetMetadataDocument? compilingTextureSettings;
    private TextureSettingsRevision compilingTextureSettingsRevision;
    private TextureAsset? compilingTextureSettingsSnapshot;
    private string? compilingTextureSettingsPath;
    private readonly Dictionary<string, (ContentManager Cache, Texture Texture)> refreshedSceneTextures = new(StringComparer.Ordinal);
    private readonly List<(ContentManager Cache, Texture Texture, int Frame)> retiredSceneTextures = [];
    private CompiledProjectContent? activeTexturePreviewContent;
    private bool IsTextureSettingsUpdating => textureSettingsBuild != null || spriteSheetBuild != null;
    private void ReleaseTextureSettingsDocument(IEditorDocument document)
    {
        ReleaseSpriteSettingsDocument(document);
        ReleaseThumbnailDocument(document);
        if (document is AssetMetadataDocument metadata)
        {
            compiledTextureSettingRevisions.Remove(metadata);
            installedTextureSettingRevisions.Remove(metadata);
            if (textureSourceWatches.Remove(metadata, out var watch)) watch.Dispose();
            if (materialPreviewDocuments.Remove(metadata.AuthoredAsset.Id)) materialPreviewRevisionSequence++;
        }
    }
    private void InvalidateCompiledTexture(string path)
    {
        if (!refreshedTextures.Remove(path, out var previous)) return;
        retiredTextureContent.Add((previous.Content, frames + 2));
    }

    private void PumpTextureSettingsUpdate()
    {
        if (!ReferenceEquals(textureSettingsWorkspace, workspace))
        {
            // Session/project replacement must stop this work before disposing its content.
            StopTextureSettingsUpdate(); textureSettingsWorkspace = workspace;
        }
        PumpSpriteSheetUpdate();
        if (texturePreview == null) activeTexturePreviewContent = null;
        PumpTextureSourceChanges();
        for (var index = retiredSceneTextures.Count - 1; index >= 0; index--)
            if (frames >= retiredSceneTextures[index].Frame && !TextureHasConsumers(retiredSceneTextures[index].Texture))
            {
                var resource = retiredSceneTextures[index]; RetireEditorPreview(new RetiredSceneTexture(this, resource.Cache, resource.Texture)); retiredSceneTextures.RemoveAt(index);
            }
        for (var index = retiredTextureContent.Count - 1; index >= 0; index--)
        {
            var retired = retiredTextureContent[index];
            if (frames < retired.Frame || ReferenceEquals(retired.Content, activeTexturePreviewContent) || MaterialPreviewUsesContent(retired.Content)
                || sceneTextureContentOwners.Values.Any(owner => ReferenceEquals(owner.Content, retired.Content))) continue;
            RetireEditorPreview(retired.Content); retiredTextureContent.RemoveAt(index);
        }
        if (textureSettingsBuild is { IsCompleted: false } && compilingTextureSettings is { } current)
        {
            WatchTextureSettingsSource(current);
            if (TextureRevision(current) != compilingTextureSettingsRevision) textureSettingsCancellation!.Cancel();
        }
        if (textureSettingsBuild?.IsCompleted == true)
        {
            var completed = textureSettingsBuild; textureSettingsBuild = null;
            var cancelled = textureSettingsCancellation!.IsCancellationRequested;
            textureSettingsCancellation.Dispose(); textureSettingsCancellation = null;
            var document = compilingTextureSettings!; compilingTextureSettings = null;
            if (cancelled || completed.IsCanceled)
            {
                if (completed.IsFaulted) _ = completed.Exception;
                // Explicit cancellation is consumed until another setting/source revision.
            }
            else Execute(() =>
            {
                try
                {
                    var output = completed.GetAwaiter().GetResult();
                    if (document.CanEditTextureSettings) WatchTextureSettingsSource(document);
                    if (TextureRevision(document) == compilingTextureSettingsRevision && document.Path == compilingTextureSettingsPath
                        && documents.AuthoredDocuments.Any(candidate => ReferenceEquals(candidate, document)))
                    {
                        InstallCompiledTexture(output, document, compilingTextureSettingsSnapshot!);
                        SetAssetThumbnailBuildStatus(document.AuthoredAsset.Id,
                            new((Stride.Core.Diagnostics.LogMessageType)Volatile.Read(ref textureSettingsBuildLevel), AssetThumbnailFailure.None));
                    }
                    else CompiledTextureSettingsBuild.ReleaseOutput(output);
                }
                catch (Exception error)
                {
                    if (TextureRevision(document) == compilingTextureSettingsRevision && document.Path == compilingTextureSettingsPath)
                    {
                        var source = textureSourceWatches.GetValueOrDefault(document)?.Source;
                        RecordAssetPreviewBuildFailure(document.AuthoredAsset.Id, error,
                            File.Exists(source) ? AssetThumbnailFailure.Broken : AssetThumbnailFailure.MissingSource);
                    }
                    throw;
                }
            });
            // Failed revisions must not trigger a failing compiler every frame.
            if (documents.AuthoredDocuments.Any(candidate => ReferenceEquals(candidate, document)))
                compiledTextureSettingRevisions[document] = compilingTextureSettingsRevision;
            compilingTextureSettingsSnapshot = null; compilingTextureSettingsPath = null;
        }
        if (!CanStartAuthoringPreviewUpdate) return;
        AssetMetadataDocument? pending = null;
        foreach (var authoredDocument in documents.AuthoredDocuments)
            if (authoredDocument is AssetMetadataDocument { CanEditTextureSettings: true } candidate)
            {
                var watch = WatchTextureSettingsSource(candidate); var revision = TextureRevision(candidate);
                if (revision == compiledTextureSettingRevisions.GetValueOrDefault(candidate))
                {
                    if (compiledTextureSettingRevisions.ContainsKey(candidate)) continue;
                    if (!refreshedTextures.ContainsKey(candidate.Path)
                        && Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(workspace.FindAsset(candidate.AuthoredAsset.Id).Location.ToString()))
                    { compiledTextureSettingRevisions[candidate] = revision; continue; }
                }
                if (watch.Pending != revision) { watch.Pending = revision; watch.Debounce = DateTime.UtcNow.AddMilliseconds(220); continue; }
                if (DateTime.UtcNow < watch.Debounce) continue;
                pending = candidate; break;
            }
        if (pending == null) return;
        compilingTextureSettings = pending; compilingTextureSettingsRevision = TextureRevision(pending);
        compilingTextureSettingsPath = pending.Path;
        compilingTextureSettingsSnapshot = AssetCloner.Clone(pending.TextureSettings!);
        textureSettingsCancellation = new CancellationTokenSource(); var token = textureSettingsCancellation.Token;
        var item = workspace.FindAsset(pending.AuthoredAsset.Id); var sourceWatch = textureSourceWatches[pending];
        var parameters = new TexturePreviewBuildParameters
        {
            Url = item.Location.ToString(), SourcePath = sourceWatch.Source, Platform = PlatformType.Windows,
            GraphicsPlatform = sourceWatch.Context.GraphicsPlatform, GraphicsProfile = sourceWatch.Context.Profile,
            TextureQuality = sourceWatch.Context.Quality, ColorSpace = sourceWatch.Context.ColorSpace,
        };
        var snapshot = compilingTextureSettingsSnapshot;
        textureSettingsBuildLevel = (int)Stride.Core.Diagnostics.LogMessageType.Info;
        textureSettingsBuild = Task.Run(() => CompiledTextureSettingsBuild.Build(snapshot, parameters, token, state =>
        { Volatile.Write(ref textureSettingsBuildState, state); AccumulatePreviewBuildLevel(ref textureSettingsBuildLevel, state); RecordBuildOutput(state); }), token);
    }
    private void InstallCompiledTexture(string output, AssetMetadataDocument document, TextureAsset authored)
    {
        if (!ReferenceEquals(textureSettingsWorkspace, workspace) || workspace == null) return;
        var item = workspace.FindAsset(document.Path);
        var content = new CompiledProjectContent(this, output, () => CompiledTextureSettingsBuild.ReleaseOutput(output));
        TextureAssetPreview? next = null;
        ContentManager? sceneCache = null; Texture? sceneTexture = null;
        var installed = false;
        try
        {
            if (texturePreview != null && string.Equals(assetPreviewPath, document.Path, StringComparison.OrdinalIgnoreCase))
            {
                var colorSpace = textureSourceWatches[document].Context.ColorSpace;
                next = new TextureAssetPreview(this, renderer!, content, item.Location.ToString(), authored, colorSpace);
            }
            var url = item.Location.ToString();
            sceneCache = content.CreateObjectCache(); sceneTexture = sceneCache.Load<Texture>(url, ContentManagerLoaderSettings.StreamingDisabled);
            RetainSceneTextureContent(sceneTexture, content);
            if (refreshedSceneTextures.Remove(url, out var oldTexture)) retiredSceneTextures.Add((oldTexture.Cache, oldTexture.Texture, frames + 2));
            refreshedSceneTextures[url] = (sceneCache, sceneTexture); sceneCache = null; sceneTexture = null;
            if (refreshedTextures.Remove(document.Path, out var previous)) retiredTextureContent.Add((previous.Content, frames + 2));
            refreshedTextures[document.Path] = (content, authored);
            installed = true;
            var contentIndex = Content.FileProvider.ObjectDatabase.ContentIndexMap;
            contentIndex.TryGetValue(url, out var rootId); contentIndex.TryGetValue(url + "_Data", out var dataId);
            installedTextureSettingRevisions[document] = new(compilingTextureSettingsRevision, url, rootId, dataId);
            InvalidateAssetThumbnails(document.AuthoredAsset.Id);
            InvalidateMaterialCompiledDependencies(content, document.AuthoredAsset.Id);
            if (next != null)
            {
                if (texturePreview != null) RetireEditorPreview(texturePreview); texturePreview = next; next = null;
                activeTexturePreviewContent = content; reportedTexturePreviewError = null;
            }
            var replacement = refreshedSceneTextures[url].Texture;
            foreach (var view in SnapshotSceneResourceViews())
                view.Viewport.ReplaceRuntimeTexture(url, replacement, view.Document);
            modelPreview?.ReplaceTexture(url, replacement); materialPreview?.ReplaceTexture(url, replacement);
        }
        catch
        {
            if (next != null) RetireEditorPreview(next);
            if (sceneTexture != null) RetireEditorPreview(new RetiredSceneTexture(this, sceneCache!, sceneTexture));
            // Once published, runtime consumers may already reference this texture.
            // Keep its cache/bundle owned even if a later consumer adapter rejects an update.
            if (!installed) RetireEditorPreview(content);
            // Actual consumer checks retain prior textures until failed bindings are replaced.
            throw;
        }
    }
    private (CompiledProjectContent Content, TextureAsset Asset) TexturePreviewSource(string path, TextureAsset asset)
        => refreshedTextures.TryGetValue(path, out var compiled) ? compiled : (projectContent!, asset);
    private bool HasCurrentCompiledTexture(IAssetTagDocument document)
    {
        if (document is not AssetMetadataDocument { TextureSettings: not null } texture) return false;
        var revision = TextureRevision(texture);
        if (revision == default && !refreshedTextures.ContainsKey(texture.Path)) return true;
        if (!installedTextureSettingRevisions.TryGetValue(texture, out var installed) || installed.Revision != revision) return false;
        var index = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        index.TryGetValue(installed.Url, out var root); index.TryGetValue(installed.Url + "_Data", out var data);
        return root == installed.Root && data == installed.Data;
    }

    private void DrawTextureSettingsUpdate()
    {
        if (textureSettingsBuild == null) return;
        if (Gui.Begin("Updating asset preview", Hexa.NET.ImGui.ImGuiWindowFlags.NoCollapse))
        {
            var state = Volatile.Read(ref textureSettingsBuildState);
            Gui.TextUnformatted(state?.Phase ?? "Preparing build");
            if (Gui.Button("Cancel update")) textureSettingsCancellation!.Cancel();
        }
        ImGuiScopes.EndWindow();
    }
    private void StopTextureSettingsUpdate()
    {
        StopSpriteSheetUpdate();
        textureSettingsCancellation?.Cancel();
        if (textureSettingsBuild != null)
            try { CompiledTextureSettingsBuild.ReleaseOutput(textureSettingsBuild.GetAwaiter().GetResult()); } catch (Exception) { }
        textureSettingsCancellation?.Dispose(); textureSettingsCancellation = null;
        textureSettingsBuild = null; compilingTextureSettings = null; textureSettingsBuildState = null;
        compilingTextureSettingsSnapshot = null; compilingTextureSettingsPath = null;
        compiledTextureSettingRevisions.Clear();
        installedTextureSettingRevisions.Clear();
        ClearTextureSourceWatches();
    }
    private void ClearCompiledTextures()
    {
        ClearCompiledFonts();
        ClearCompiledSpriteSheets();
        StopTextureSettingsUpdate();
        foreach (var resource in refreshedSceneTextures.ToArray())
        {
            ReleaseSceneTextureContent(resource.Value.Cache, resource.Value.Texture);
            refreshedSceneTextures.Remove(resource.Key);
        }
        foreach (var resource in retiredSceneTextures.ToArray())
        {
            ReleaseSceneTextureContent(resource.Cache, resource.Texture);
            retiredSceneTextures.Remove(resource);
        }
        foreach (var source in refreshedTextures.Values) source.Content.Dispose();
        foreach (var source in retiredTextureContent) source.Content.Dispose();
        refreshedTextures.Clear(); retiredTextureContent.Clear(); activeTexturePreviewContent = null;
        materialPreviewDocuments.Clear(); materialPreviewRevision = pendingMaterialPreviewRevision = materialPreviewRevisionSequence = 0;
        materialCompiledContent = null;
    }
}
