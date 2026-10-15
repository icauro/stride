using System.Text.Json;
using Stride.Assets.Effect;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Physics.Assets;
using Stride.Assets.Media;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class OwnedCatalogPreview(IDisposable preview, CompiledProjectContent content) : IDisposable
    {
        private bool previewReleased, contentReleased;
        public void Dispose()
        {
            if (!previewReleased) { preview.Dispose(); previewReleased = true; }
            if (!contentReleased) { content.Dispose(); contentReleased = true; }
        }
    }
    private readonly Dictionary<IDisposable, OwnedCatalogPreview> ownedCatalogPreviews = new(ReferenceEqualityComparer.Instance);
    private readonly AuthoredImportPollingCache catalogPolling = new();
    private AssetEntry? requestedCatalogPreview;
    private bool requestedCatalogExternal;
    private ProjectWorkspace? catalogCompilationWorkspace;
    private Task<string>? catalogBuild;
    private CancellationTokenSource? catalogCancellation;
    private MediaCompilationSnapshot? compilingCatalogSnapshot;
    private AssetEntry? compilingCatalogEntry;
    private string? installedCatalogRevision, attemptedCatalogRevision, pendingCatalogRevision, catalogCaptureError;
    private DateTime catalogCheckAt, catalogDebounce;
    private int catalogBuildLevel, detachedCatalogBuilds;
    private bool IsCatalogPreviewUpdating => catalogBuild != null || Volatile.Read(ref detachedCatalogBuilds) != 0;

    private void RequestCatalogPreviewUpdate(AssetEntry entry, bool external = false)
    {
        if (requestedCatalogPreview?.Id == entry.Id && ReferenceEquals(catalogCompilationWorkspace, workspace)) return;
        StopCatalogPreviewUpdate();
        requestedCatalogPreview = entry; catalogCompilationWorkspace = workspace;
        requestedCatalogExternal = external;
        installedCatalogRevision = attemptedCatalogRevision = pendingCatalogRevision = catalogCaptureError = null;
        catalogCheckAt = DateTime.MinValue; catalogPolling.Clear();
    }
    private MediaCompilationSnapshot CaptureCatalogPreview(AssetEntry entry, bool verify = false)
        => CaptureAuthoredPreviewCompilation(entry, verify, workspace!.Session.Packages.SelectMany(package => package.Assets)
            .Where(item => item.Asset is EffectShaderAsset).Select(item => item.Id).ToArray());

    private void PumpCatalogPreviewUpdate()
    {
        if (workspace == null || projectContent == null || !ReferenceEquals(catalogCompilationWorkspace, workspace))
        { StopCatalogPreviewUpdate(); return; }
        if (catalogBuild is { IsCompleted: true } completed)
        {
            catalogBuild = null; var cancelled = catalogCancellation!.IsCancellationRequested;
            catalogCancellation.Dispose(); catalogCancellation = null;
            var capture = compilingCatalogSnapshot!; var entry = compilingCatalogEntry!;
            if (cancelled && attemptedCatalogRevision == capture.Revision) attemptedCatalogRevision = null;
            compilingCatalogSnapshot = null; compilingCatalogEntry = null;
            string? output = null;
            try
            {
                output = completed.GetAwaiter().GetResult();
                var current = workspace.Assets.FirstOrDefault(candidate => candidate.Id == entry.Id);
                if (!cancelled && current != null && requestedCatalogPreview?.Id == entry.Id && selectedAssetPath == current.Path
                    && CaptureCatalogPreview(current, true).Revision == capture.Revision)
                {
                    InstallCatalogPreview(current, capture, ref output);
                    installedCatalogRevision = capture.Revision;
                    SetAssetThumbnailBuildStatus(current.Id, new((LogMessageType)Volatile.Read(ref catalogBuildLevel), AssetThumbnailFailure.None));
                    InvalidateAssetThumbnails(current.Id);
                }
            }
            catch (OperationCanceledException) when (cancelled) { }
            catch (Exception error)
            { if (!RoutePreviewGraphicsFailure(error) && !cancelled && requestedCatalogPreview?.Id == entry.Id) RecordAssetPreviewBuildFailure(entry.Id, error, AssetThumbnailFailure.Broken); }
            finally { if (output != null) CompiledMediaBuild.ReleaseOutput(output); }
        }
        if (requestedCatalogPreview == null || DateTime.UtcNow < catalogCheckAt) return;
        if (requestedCatalogExternal && !HasExternalAssetPreview(requestedCatalogPreview.AssetType)) { StopCatalogPreviewUpdate(); return; }
        var published = workspace.Assets.FirstOrDefault(entry => entry.Id == requestedCatalogPreview.Id);
        if (published == null || selectedAssetPath != published.Path) { StopCatalogPreviewUpdate(); return; }
        requestedCatalogPreview = published; catalogCheckAt = DateTime.UtcNow.AddMilliseconds(250);
        MediaCompilationSnapshot snapshot;
        try
        {
            var inputs = AuthoredImportPollingCache.CaptureInputs(workspace, authoring);
            snapshot = catalogPolling.Capture(workspace, authoring, published, inputs,
                _ => CaptureCatalogPreview(published), includeInboundDependencies: false).Snapshot;
            catalogCaptureError = null;
        }
        catch (Exception error)
        {
            if (catalogCaptureError != error.Message)
            { catalogCaptureError = error.Message; RecordAssetPreviewBuildFailure(published.Id, error, AssetThumbnailFailure.Broken); }
            catalogCancellation?.Cancel(); return;
        }
        if (catalogBuild != null)
        { if (compilingCatalogSnapshot?.Revision != snapshot.Revision) catalogCancellation!.Cancel(); return; }
        if (snapshot.Revision == installedCatalogRevision || snapshot.Revision == attemptedCatalogRevision) return;
        if (pendingCatalogRevision != snapshot.Revision)
        { pendingCatalogRevision = snapshot.Revision; catalogDebounce = DateTime.UtcNow.AddMilliseconds(180); }
        if (DateTime.UtcNow < catalogDebounce || !CanStartAuthoringPreviewUpdate) return;
        try { snapshot = CaptureCatalogPreview(published, true); }
        catch (Exception error) { RecordAssetPreviewBuildFailure(published.Id, error, AssetThumbnailFailure.Broken); return; }
        // The generic production compiler accepts any registered real root. Clone
        // its transport objects so a child build cannot mutate the polling snapshot.
        var parameters = JsonSerializer.Deserialize<MediaPreviewBuildParameters>(JsonSerializer.Serialize(snapshot.Parameters))!;
        var assets = snapshot.Assets.Select(asset => new MediaAssetSnapshot(new MediaPreviewAssetInput {
            Id = asset.Input.Id, Url = asset.Input.Url, AssetPath = asset.Input.AssetPath,
            LocalUrl = asset.Input.LocalUrl, SourcePackagePath = asset.Input.SourcePackagePath, PackageDependencies = asset.Input.PackageDependencies.ToArray(), AssetNamespace = asset.Input.AssetNamespace }, asset.Bytes, asset.Extension)).ToArray();
        var buildSnapshot = snapshot with { Parameters = parameters, Assets = assets };
        attemptedCatalogRevision = snapshot.Revision; compilingCatalogSnapshot = snapshot; compilingCatalogEntry = published;
        catalogBuildLevel = (int)LogMessageType.Info;
        catalogCancellation = new(); var token = catalogCancellation.Token;
        catalogBuild = Task.Run(() => CompiledMediaBuild.Build(buildSnapshot, token, state =>
        { AccumulatePreviewBuildLevel(ref catalogBuildLevel, state); RecordBuildOutput(state); }), token);
    }

    private void InstallCatalogPreview(AssetEntry entry, MediaCompilationSnapshot snapshot, ref string? output)
    {
        var item = workspace!.FindAsset(entry.Id); var ownedOutput = output!;
        CompiledProjectContent? bundle = null; IDisposable? preview = null;
        try
        {
            bundle = new(this, ownedOutput, () => CompiledMediaBuild.ReleaseOutput(ownedOutput)); output = null;
            if (HasExternalAssetPreview(entry.AssetType))
            {
                var nativeBundle = bundle; bundle = null;
                BeginExternalAssetPreview(entry, nativeBundle, true, snapshot.Revision); return;
            }
            var color = snapshot.Parameters.ColorSpace; var url = item.Location.ToString();
            var restore = catalogPreviewPanel?.CaptureViewRestore();
            if (item.Asset is HeightmapAsset)
            {
                var heightmap = new HeightmapAssetPreview(this, renderer!, bundle, url, color); preview = heightmap;
                ownedCatalogPreviews.Add(heightmap, new(heightmap, bundle)); bundle = null;
                CloseLegacyAssetPreviews(); CatalogPreviewPanel.SetHeightmap(heightmap);
            }
            else
            {
                var (entity, kind) = item.Asset switch
                {
                    PrefabAsset => (EntityAssetPreview.Prefab(this, renderer!, bundle, url, snapshot.Hdr, color, Stride.Core.Assets.PackageTypeScope.For(item.Package)), "prefab-preview"),
                    PrefabModelAsset => (EntityAssetPreview.PrefabModel(this, renderer!, bundle, url, snapshot.Hdr, color, Stride.Core.Assets.PackageTypeScope.For(item.Package)), "prefab-model-preview"),
                    SpriteStudioModelAsset => (EntityAssetPreview.SpriteStudio(this, renderer!, bundle, url, snapshot.Hdr, color, Stride.Core.Assets.PackageTypeScope.For(item.Package)), "spritestudio-preview"),
                    _ => throw new InvalidOperationException("This asset has no registered catalog preview.")
                };
                preview = entity; ownedCatalogPreviews.Add(entity, new(entity, bundle)); bundle = null;
                CloseLegacyAssetPreviews(); CatalogPreviewPanel.SetEntity(entity, kind);
            }
            preview = null; restore?.Invoke(); assetPreviewPath = entry.Path;
        }
        finally
        {
            if (preview != null) ReleaseCatalogPreviewResource(preview, true);
            bundle?.Dispose();
        }
    }
    private void ReleaseCatalogPreviewResource(IDisposable preview, bool immediate)
    {
        IDisposable owner = ownedCatalogPreviews.Remove(preview, out var owned) ? owned : preview;
        if (!immediate) { RetireEditorPreview(owner); return; }
        try { owner.Dispose(); }
        catch { RetireEditorPreview(owner); throw; }
    }
    private void StopCatalogPreviewUpdate()
    {
        var build = catalogBuild; var cancellation = catalogCancellation; cancellation?.Cancel();
        catalogBuild = null; catalogCancellation = null; compilingCatalogSnapshot = null; compilingCatalogEntry = null;
        if (requestedCatalogExternal) { externalPreviewRestoreState = null; externalPreviewReattachmentBuild = null; }
        requestedCatalogPreview = null; catalogCompilationWorkspace = null; catalogPolling.Clear();
        requestedCatalogExternal = false;
        if (build == null) { cancellation?.Dispose(); return; }
        Interlocked.Increment(ref detachedCatalogBuilds);
        _ = build.ContinueWith(completed =>
        {
            try { if (completed.Status == TaskStatus.RanToCompletion) CompiledMediaBuild.ReleaseOutput(completed.Result);
                else if (completed.IsFaulted) _ = completed.Exception; }
            catch (Exception error) { outputLog.Append("Project", LogMessageType.Error, "Asset preview", error.Message, error.ToString()); }
            finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedCatalogBuilds); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
