using Stride.Assets.Sprite;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Storage;
using Stride.Core.Serialization;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<AssetMetadataDocument, SpriteCompilationRevision> compiledSpriteRevisions = [];
    private sealed class InstalledSpriteSheet
    {
        public required CompiledSpriteSheetContent Content;
        public required AssetId AssetId;
        public required (string Url, ObjectId Id)[] Identity;
        public long CheckedEpoch;
        public bool Current = true;
        public bool PendingBinding;
        public long BindingRetryAt;
        public string? BindingFailure;
    }
    private readonly Dictionary<string, InstalledSpriteSheet> refreshedSpriteSheets = new(StringComparer.Ordinal);
    private long spriteCompiledContentEpoch;
    private void NotifySpriteCompiledContentInstalled() => spriteCompiledContentEpoch++;
    private bool HasCurrentSpriteSheetContent(InstalledSpriteSheet installed)
    {
        if (installed.CheckedEpoch == spriteCompiledContentEpoch) return installed.Current;
        installed.CheckedEpoch = spriteCompiledContentEpoch;
        var map = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        installed.Current = true;
        foreach (var entry in installed.Identity)
            if (!map.TryGetValue(entry.Url, out var id) || id != entry.Id) { installed.Current = false; break; }
        return installed.Current;
    }
    private readonly List<(CompiledSpriteSheetContent Content, int Frame)> retiredSpriteSheets = [];
    private ProjectWorkspace? spriteCompilationWorkspace;
    private Task<string>? spriteSheetBuild;
    private CancellationTokenSource? spriteSheetCancellation;
    private ProjectLoadState? spriteSheetBuildState;
    private AssetMetadataDocument? compilingSpriteDocument;
    private SpriteCompilationRevision compilingSpriteRevision;
    private string? compilingSpritePath;
    private int spriteSheetBuildLevel;

    private void ReleaseSpriteSettingsDocument(IEditorDocument document)
    {
        if (document is not AssetMetadataDocument metadata) return;
        if (metadata.SpriteSettings != null)
        { selectionHistory.UnregisterSprites(metadata.AuthoredAsset.Id); metadata.ClearNativeSpriteSelection(); }
        ReleaseSpriteFrameList(metadata);
        if (ReferenceEquals(requestedSpriteSheetDocument, metadata) || ReferenceEquals(spriteSheetPreviewDocument, metadata)) CloseSpriteSheetPreview();
        ReleaseSpriteImageSizes(metadata);
        if (ReferenceEquals(activeSpriteFrameDrag?.Document, metadata)) { activeSpriteFrameDrag = null; deliveredSpriteFrameDrag = null; }
        if (ReferenceEquals(spriteClipboardDocument, metadata)) ClearSpriteClipboardState();
        ReleaseSpriteCanvas(metadata);
        compiledSpriteRevisions.Remove(metadata);
        if (spriteSourceWatches.Remove(metadata, out var watch)) watch.Dispose();
        if (ReferenceEquals(compilingSpriteDocument, metadata)) spriteSheetCancellation?.Cancel();
    }
    private bool CompiledSpriteSheetHasConsumers(CompiledSpriteSheetContent content)
        => content.Sheet != null && (SceneViewsUseImportedContent([content.Sheet])
            || content.SnapshotOwnedTextures().Any(TextureHasConsumers)
            || ReferenceEquals(spriteSheetPreview?.SourceContent, content.Bundle)
            || retiredEditorPreviews.Any(owner => !owner.PreviewDisposed && owner.Preview is SpriteSheetAssetPreview preview
                && !preview.IsDisposed && ReferenceEquals(preview.SourceContent, content.Bundle)));

    private void PumpSpriteSheetUpdate()
    {
        PumpWholeSpriteImages();
        if (!ReferenceEquals(spriteCompilationWorkspace, workspace))
        { ClearSpriteSourceActions(); ClearCompiledSpriteSheets(); spriteCompilationWorkspace = workspace; }
        while (changedSpriteSources.TryDequeue(out var id))
            foreach (var pair in spriteSourceWatches)
                if (pair.Key.AuthoredAsset.Id == id) pair.Value.Revision++;
        PumpSpriteImageSizes();
        for (var index = retiredSpriteSheets.Count - 1; index >= 0; index--)
            if (frames >= retiredSpriteSheets[index].Frame && !CompiledSpriteSheetHasConsumers(retiredSpriteSheets[index].Content))
            { RetireEditorPreview(retiredSpriteSheets[index].Content); retiredSpriteSheets.RemoveAt(index); }
        if (spriteSheetBuild is { IsCompleted: false } && compilingSpriteDocument is { } current)
        {
            WatchSpriteSources(current);
            if (SpriteRevision(current) != compilingSpriteRevision || current.Path != compilingSpritePath) spriteSheetCancellation!.Cancel();
        }
        if (spriteSheetBuild?.IsCompleted == true)
        {
            var completed = spriteSheetBuild; spriteSheetBuild = null;
            var cancelled = spriteSheetCancellation!.IsCancellationRequested;
            spriteSheetCancellation.Dispose(); spriteSheetCancellation = null;
            var authored = compilingSpriteDocument!; compilingSpriteDocument = null;
            Execute(() =>
            {
                string? output = null;
                try
                {
                    output = completed.GetAwaiter().GetResult();
                    if (authored.SpriteSettings != null && documents.AuthoredDocuments.Any(document => ReferenceEquals(document, authored)))
                        WatchSpriteSources(authored);
                    if (cancelled || SpriteRevision(authored) != compilingSpriteRevision || authored.Path != compilingSpritePath
                        || !documents.AuthoredDocuments.Any(document => ReferenceEquals(document, authored)))
                    { CompiledSpriteSheetBuild.ReleaseOutput(output); return; }
                    InstallCompiledSpriteSheet(output, authored); output = null;
                    SetAssetThumbnailBuildStatus(authored.AuthoredAsset.Id, new((LogMessageType)Volatile.Read(ref spriteSheetBuildLevel), AssetThumbnailFailure.None));
                }
                catch (OperationCanceledException) when (cancelled) { }
                catch (Exception error)
                {
                    if (!cancelled && SpriteRevision(authored) == compilingSpriteRevision && authored.Path == compilingSpritePath)
                        RecordAssetPreviewBuildFailure(authored.AuthoredAsset.Id, error, AssetThumbnailFailure.Broken);
                    else if (completed.IsFaulted) _ = completed.Exception;
                    if (!cancelled) throw;
                }
            });
            if (documents.AuthoredDocuments.Any(document => ReferenceEquals(document, authored))) compiledSpriteRevisions[authored] = compilingSpriteRevision;
            compilingSpritePath = null;
        }
        if (!CanStartAuthoringPreviewUpdate) return;
        RetryCompiledSpriteSheetBindings();
        foreach (var document in documents.AuthoredDocuments)
        {
            if (document is not AssetMetadataDocument { SpriteSettings: not null } authored
                || !authored.CanEditSpriteSettings && !ReferenceEquals(requestedSpriteSheetDocument, authored)) continue;
            var watch = WatchSpriteSources(authored); var revision = SpriteRevision(authored);
            var indexMap = Content.FileProvider.ObjectDatabase.ContentIndexMap;
            if (compiledSpriteRevisions.TryGetValue(authored, out var attempted) && attempted == revision)
            {
                if (!refreshedSpriteSheets.TryGetValue(watch.Url, out var installed) || HasCurrentSpriteSheetContent(installed)) continue;
                // A full project bundle superseded the unsaved sheet mount.
                compiledSpriteRevisions.Remove(authored);
            }
            if (!compiledSpriteRevisions.ContainsKey(authored) && revision == default && !refreshedSpriteSheets.ContainsKey(watch.Url)
                && indexMap.Contains(watch.Url) && !ReferenceEquals(requestedSpriteSheetDocument, authored)) { compiledSpriteRevisions[authored] = revision; continue; }
            if (watch.Pending != revision || watch.Debounce == default)
            { watch.Pending = revision; watch.Debounce = DateTime.UtcNow.AddMilliseconds(220); continue; }
            if (DateTime.UtcNow < watch.Debounce) continue;
            compilingSpriteDocument = authored; compilingSpriteRevision = revision; compilingSpritePath = authored.Path;
            var snapshot = CompiledSpriteSheetBuild.Capture(authored);
            var parameters = new SpriteSheetPreviewBuildParameters
            {
                AssetPath = authored.Path, Url = watch.Url, Platform = PlatformType.Windows,
                GraphicsPlatform = watch.Context.GraphicsPlatform, GraphicsProfile = watch.Context.Profile,
                TextureQuality = watch.Context.Quality, ColorSpace = watch.Context.ColorSpace,
            };
            spriteSheetBuildLevel = (int)LogMessageType.Info;
            spriteSheetCancellation = new CancellationTokenSource(); var token = spriteSheetCancellation.Token;
            spriteSheetBuild = Task.Run(() => CompiledSpriteSheetBuild.Build(snapshot, parameters, token, state =>
            { Volatile.Write(ref spriteSheetBuildState, state); AccumulatePreviewBuildLevel(ref spriteSheetBuildLevel, state); RecordBuildOutput(state); }), token);
            break;
        }
    }
    private void InstallCompiledSpriteSheet(string output, AssetMetadataDocument document)
    {
        var url = spriteSourceWatches[document].Url;
        var owner = new CompiledSpriteSheetContent(this, output, url, CompiledSpriteSheetHasConsumers);
        var previous = refreshedSpriteSheets.GetValueOrDefault(url);
        var map = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        var urls = new HashSet<string>(StringComparer.Ordinal) { url };
        foreach (var sprite in owner.Sheet.Sprites)
            if (sprite.Texture != null && AttachedReferenceManager.GetUrl(sprite.Texture) is { } textureUrl)
            { urls.Add(textureUrl); if (map.Contains(textureUrl + "_Data")) urls.Add(textureUrl + "_Data"); }
        var identity = urls.Select(location => { map.TryGetValue(location, out var id); return (location, id); }).ToArray();
        NotifySpriteCompiledContentInstalled();
        var installed = new InstalledSpriteSheet { Content = owner, AssetId = document.AuthoredAsset.Id, Identity = identity, CheckedEpoch = spriteCompiledContentEpoch, PendingBinding = true };
        refreshedSpriteSheets[url] = installed;
        if (previous != null) retiredSpriteSheets.Add((previous.Content, frames + 2));
        try { BindCompiledSpriteSheet(installed); }
        catch (Exception error)
        {
            installed.BindingFailure = error.GetType().FullName + ": " + error.Message;
            installed.BindingRetryAt = Environment.TickCount64 + 250;
            // Both actual generations remain owned. Retirement uses their
            // real sheet/atlas consumers, rather than an irreversible time pin.
            throw;
        }
        InvalidateAssetThumbnails(document.AuthoredAsset.Id);
        ReloadSpriteSheetPreview(document);
    }
    private void BindCompiledSpriteSheet(InstalledSpriteSheet installed)
    {
        if (installed.Content.IsDisposed || !refreshedSpriteSheets.TryGetValue(installed.Content.Url, out var current)
            || !ReferenceEquals(current, installed)) throw new InvalidOperationException("The compiled sprite generation changed.");
        foreach (var view in SnapshotSceneResourceViews())
            view.Viewport.ReplaceRuntimeSpriteSheet(installed.Content.Url, installed.Content.Sheet, view.Document);
        installed.PendingBinding = false; installed.BindingFailure = null;
    }
    private void RetryCompiledSpriteSheetBindings()
    {
        foreach (var installed in refreshedSpriteSheets.Values.ToArray())
        {
            if (!installed.PendingBinding || Environment.TickCount64 < installed.BindingRetryAt
                || !HasCurrentSpriteSheetContent(installed)) continue;
            try
            {
                BindCompiledSpriteSheet(installed);
                var source = documents.AuthoredDocuments.OfType<AssetMetadataDocument>()
                    .FirstOrDefault(value => value.AuthoredAsset.Id == installed.AssetId && value.SpriteSettings != null);
                if (source != null && spriteSourceWatches.TryGetValue(source, out var watch) && watch.Url == installed.Content.Url)
                { InvalidateAssetThumbnails(installed.AssetId); ReloadSpriteSheetPreview(source); }
            }
            catch (Exception error)
            {
                installed.BindingRetryAt = Environment.TickCount64 + 250;
                var failure = error.GetType().FullName + ": " + error.Message;
                if (installed.BindingFailure == failure) continue;
                installed.BindingFailure = failure;
                RecordAssetPreviewRenderFailure("Sprite sheet binding", error);
            }
        }
    }
    private void ReplayCompiledSpriteSheets(EngineViewport target, EntityHierarchyDocument document)
    {
        foreach (var pair in refreshedSpriteSheets) target.ReplaceRuntimeSpriteSheet(pair.Key, pair.Value.Content.Sheet, document);
    }
    private void DrawSpriteSheetUpdate()
    {
        if (spriteSheetBuild == null) return;
        if (Gui.Begin("Updating asset preview", Hexa.NET.ImGui.ImGuiWindowFlags.NoCollapse))
        {
            Gui.TextUnformatted(Volatile.Read(ref spriteSheetBuildState)?.Phase ?? "Preparing build");
            if (Gui.Button("Cancel update")) spriteSheetCancellation!.Cancel();
        }
        ImGuiScopes.EndWindow();
    }
    private void ClearCompiledSpriteSheets()
    {
        CloseSpriteSheetPreview();
        ClearSpriteClipboardState();
        ClearSpriteCanvases();
        StopSpriteSheetUpdate();
        foreach (var pair in refreshedSpriteSheets.ToArray())
        {
            RetireEditorPreview(pair.Value.Content); refreshedSpriteSheets.Remove(pair.Key);
        }
        for (var index = retiredSpriteSheets.Count - 1; index >= 0; index--)
        {
            RetireEditorPreview(retiredSpriteSheets[index].Content); retiredSpriteSheets.RemoveAt(index);
        }
    }
    private void StopSpriteSheetUpdate()
    {
        StopWholeSpriteImages();
        spriteSheetCancellation?.Cancel();
        if (spriteSheetBuild != null)
            try { CompiledSpriteSheetBuild.ReleaseOutput(spriteSheetBuild.GetAwaiter().GetResult()); } catch (Exception) { }
        spriteSheetCancellation?.Dispose(); spriteSheetCancellation = null; spriteSheetBuild = null; spriteSheetBuildState = null;
        compilingSpriteDocument = null; compilingSpritePath = null;
        compiledSpriteRevisions.Clear(); ClearSpriteSourceWatches();
    }
}
