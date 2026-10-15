using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Serialization.Contents;
using Stride.Assets.Models;
using Stride.Assets;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed partial class ImportContentOwner : IDisposable
    {
        public required CompiledProjectContent Bundle;
        public required ContentManager Cache;
        public required EngineViewport.ImportedContent[] Values;
        public required AssetId[] AssetIds;
        public long Generation;
        public int PreviewLeases;
        public ModelAssetPreview? Preview;
        public int RetireFrame;
        private readonly HashSet<object> unloaded = new(ReferenceEqualityComparer.Instance);
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (Volatile.Read(ref PreviewLeases) != 0 || Preview is { IsDisposed: false })
                throw new PreviewResourceDrainPendingException();
            foreach (var value in Values)
                if (!unloaded.Contains(value.Value)) { Cache.Unload(value.Value); unloaded.Add(value.Value); }
            Bundle.Dispose(); disposed = true;
        }
    }
    private sealed class ImportedContentLease : IDisposable
    {
        private ImportContentOwner? owner;
        public CompiledProjectContent Bundle => owner?.Bundle ?? throw new ObjectDisposedException(nameof(ImportedContentLease));
        public long Generation => owner?.Generation ?? throw new ObjectDisposedException(nameof(ImportedContentLease));
        public ImportedContentLease(ImportContentOwner owner) { this.owner = owner; Interlocked.Increment(ref owner.PreviewLeases); }
        public void Dispose() { var released = Interlocked.Exchange(ref owner, null); if (released != null) Interlocked.Decrement(ref released.PreviewLeases); }
    }
    private long importedContentGeneration;
    private long GetImportedContentGeneration(AssetId id)
        => importedContentOwners.Values.Where(owner => owner.AssetIds.Contains(id)).Select(owner => owner.Generation).DefaultIfEmpty(0).Max();
    private ImportedContentLease? AcquireImportedContent(AssetId id)
    {
        var owner = importedContentOwners.Values.Where(owner => owner.AssetIds.Contains(id)).OrderByDescending(owner => owner.Generation).FirstOrDefault();
        return owner == null ? null : new(owner);
    }
    private sealed record ImportCompilationRequest(ProjectWorkspace Workspace, AssetEntry Entry,
        AssetId[] Roots, MediaCompilationSnapshot Snapshot);
    private readonly Dictionary<AssetId, string> installedImportRevisions = [];
    private readonly Dictionary<AssetId, string> attemptedImportRevisions = [];
    private readonly Dictionary<AssetId, (string Revision, DateTime Since)> pendingImportRevisions = [];
    private readonly Dictionary<AssetId, ImportContentOwner> importedContentOwners = [];
    private readonly List<ImportContentOwner> retiredImportContentOwners = [];
    private ProjectWorkspace? importSettingsWorkspace;
    private Task<string>? importSettingsBuild;
    private CancellationTokenSource? importSettingsCancellation;
    private ImportCompilationRequest? importCompilationRequest;
    private DateTime importSettingsCheckAt;
    private int detachedImportBuilds;
    private int importSettingsBuildLevel;
    private readonly Dictionary<AssetId, string> importCaptureErrors = [];
    private readonly AuthoredImportPollingCache importPollingCache = new();
    private long importPollingInputCaptureCount;
    private int importPollingEligibleOwnerCount;
    private bool IsImportSettingsUpdating => importSettingsBuild != null || Volatile.Read(ref detachedImportBuilds) != 0;

    private void PumpImportSettingsUpdate()
    {
        foreach (var owner in retiredImportContentOwners.Where(owner => frames >= owner.RetireFrame
            && Volatile.Read(ref owner.PreviewLeases) == 0
            && (owner.Preview == null || owner.Preview.IsDisposed)
            && !MaterialPreviewUsesContent(owner.Bundle)
            && !SceneViewsUseImportedContent(owner.Values.Select(value => value.Value).ToArray())).ToArray())
        { RetireEditorPreview(owner); retiredImportContentOwners.Remove(owner); }
        if (!ReferenceEquals(importSettingsWorkspace, workspace))
        { StopImportSettingsUpdate(); importSettingsWorkspace = workspace; }
        if (importSettingsBuild is { IsCompleted: true } complete)
        {
            var request = importCompilationRequest!; var cancelled = importSettingsCancellation!.IsCancellationRequested;
            importSettingsBuild = null; importCompilationRequest = null;
            importSettingsCancellation.Dispose(); importSettingsCancellation = null;
            string? output = null;
            try
            {
                output = complete.GetAwaiter().GetResult();
                if (!cancelled && ReferenceEquals(workspace, request.Workspace)
                    && workspace!.Assets.Any(entry => entry.Id == request.Entry.Id)
                    && authoring?.Find(request.Entry.Id)?.Document is not ProceduralAssetDocument { HasPendingCompilationEdits: true }
                    && AuthoredImportDependencyClosure.Find(workspace, authoring, request.Entry.Id).SequenceEqual(request.Roots)
                    && CaptureAuthoredPreviewCompilation(request.Entry, true, request.Roots).Revision == request.Snapshot.Revision)
                {
                    InstallImportedContent(request, ref output);
                    installedImportRevisions[request.Entry.Id] = request.Snapshot.Revision;
                    foreach (var id in request.Roots)
                    {
                        InvalidateAssetThumbnails(id);
                        SetAssetThumbnailBuildStatus(id, new((LogMessageType)Volatile.Read(ref importSettingsBuildLevel), AssetThumbnailFailure.None));
                    }
                }
                else attemptedImportRevisions.Remove(request.Entry.Id);
            }
            catch (OperationCanceledException) when (cancelled) { attemptedImportRevisions.Remove(request.Entry.Id); }
            catch (Exception error)
            {
                if (!cancelled && ReferenceEquals(workspace, request.Workspace))
                    RecordAssetPreviewBuildFailure(request.Entry.Id, error, AssetThumbnailFailure.Broken);
            }
            finally { if (output != null) CompiledImportAssetBuild.ReleaseOutput(output); }
        }
        if (workspace == null || authoring == null || DateTime.UtcNow < importSettingsCheckAt) return;
        importSettingsCheckAt = DateTime.UtcNow.AddMilliseconds(250);
        string? inputs = null;
        importPollingEligibleOwnerCount = 0;
        foreach (var state in authoring.PublishedStates.ToArray())
        {
            if (state.AssetId is not { } id || state.Document is not IAssetTagDocument document) continue;
            if (document is AssetMetadataDocument metadata)
            { if (!metadata.HasImportSettings && metadata.MaterialSettings == null) continue; }
            else if (document is not ProceduralAssetDocument) continue;
            if (document is ProceduralAssetDocument { HasPendingCompilationEdits: true }) continue;
            var entry = workspace.Assets.FirstOrDefault(candidate => candidate.Id == id);
            if (entry == null) continue;
            try
            {
                // Initialize the actual graph once so replay/child edits participate in canonical revision capture.
                if (document is AssetMetadataDocument importMetadata) _ = importMetadata.ImportSettingsFields;
                importPollingEligibleOwnerCount++;
                if (inputs == null)
                {
                    importPollingInputCaptureCount++;
                    inputs = AuthoredImportPollingCache.CaptureInputs(workspace, authoring);
                }
                var cached = importPollingCache.Capture(workspace, authoring, entry, inputs,
                    roots => CaptureAuthoredPreviewCompilation(entry, false, roots));
                var roots = cached.Roots; var snapshot = cached.Snapshot;
                importCaptureErrors.Remove(id);
                if (!installedImportRevisions.TryGetValue(id, out var installed) && !document.IsDirty
                    && Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(snapshot.ClipUrl))
                { installedImportRevisions[id] = snapshot.Revision; continue; }
                if (importSettingsBuild != null)
                {
                    if (importCompilationRequest!.Entry.Id == id && snapshot.Revision != importCompilationRequest.Snapshot.Revision)
                        importSettingsCancellation!.Cancel();
                    continue;
                }
                if (snapshot.Revision == installed || attemptedImportRevisions.GetValueOrDefault(id) == snapshot.Revision) continue;
                if (!pendingImportRevisions.TryGetValue(id, out var pending) || pending.Revision != snapshot.Revision)
                { pendingImportRevisions[id] = (snapshot.Revision, DateTime.UtcNow); continue; }
                if ((DateTime.UtcNow - pending.Since).TotalMilliseconds < 180 || !CanStartAuthoringPreviewUpdate) continue;
                snapshot = CaptureAuthoredPreviewCompilation(entry, true, roots);
                attemptedImportRevisions[id] = snapshot.Revision;
                importCompilationRequest = new(workspace, entry, roots, snapshot);
                importSettingsCancellation = new(); var token = importSettingsCancellation.Token;
                importSettingsBuildLevel = (int)LogMessageType.Info;
                importSettingsBuild = Task.Run(() => CompiledImportAssetBuild.Build(snapshot, token, progress =>
                {
                    if (token.IsCancellationRequested && progress.CompilerEvent == CompilerRunEvent.None) return;
                    AccumulatePreviewBuildLevel(ref importSettingsBuildLevel, progress); RecordBuildOutput(progress);
                }), token);
                break;
            }
            catch (Exception error)
            {
                if (importCaptureErrors.GetValueOrDefault(id) != error.ToString())
                { importCaptureErrors[id] = error.ToString(); RecordAssetPreviewBuildFailure(id, error, AssetThumbnailFailure.Broken); }
            }
        }
    }

    private void InstallImportedContent(ImportCompilationRequest request, ref string? output)
    {
        CompiledProjectContent? bundle = null; ContentManager? cache = null; ModelAssetPreview? preview = null;
        var values = new List<EngineViewport.ImportedContent>();
        ImportContentOwner? candidateOwner = null;
        try
        {
            var ownedOutput = output!;
            bundle = new(this, ownedOutput, () => CompiledImportAssetBuild.ReleaseOutput(ownedOutput));
            output = null; cache = bundle.CreateObjectCache();
            foreach (var id in request.Roots)
            {
                var item = workspace!.Session.FindAsset(id)!;
                if (item.Asset is AssetComposite or Stride.Assets.GameSettingsAsset) continue;
                var type = AssetRegistry.GetContentType(item.Asset.GetType());
                if (type == null) continue;
                var url = item.Location.ToString();
                values.Add(new(type, url, cache.Load(type, url, ContentManagerLoaderSettings.StreamingDisabled)));
            }
            var previewEntry = workspace!.Assets.FirstOrDefault(entry => entry.Path == assetPreviewPath && request.Roots.Contains(entry.Id));
            if (request.Entry.Path == selectedAssetPath && request.Entry.AssetType == typeof(ProceduralModelAsset)) previewEntry = request.Entry;
            var currentItem = previewEntry == null ? null : workspace.FindAsset(previewEntry.Id);
            if (currentItem?.Asset is ModelAsset or Stride.Assets.Models.PrefabModelAsset or ProceduralModelAsset)
            {
                var settings = currentItem.Package.GetGameSettingsAssetOrDefault();
                preview = new(this, renderer!, bundle, currentItem.Location.ToString(), settings.GetOrCreate<EditorSettings>().RenderingMode == RenderingMode.HDR,
                    currentItem.Asset is ProceduralModelAsset, currentItem.Asset is ProceduralModelAsset procedural ? PreviewAngles(procedural.Type) : null,
                    settings.GetOrCreate<RenderingSettings>().ColorSpace);
                if (modelPreview != null) preview.Camera.RestoreState(modelPreview.Camera.CaptureState());
            }
            var owner = new ImportContentOwner { Bundle = bundle, Cache = cache, Values = values.ToArray(), Preview = preview,
                AssetIds = request.Roots, Generation = ++importedContentGeneration };
            candidateOwner = owner;
            foreach (var view in SnapshotSceneResourceViews())
            {
                RetainSceneDocumentImportOwner(view.Viewport, owner);
                view.Viewport.ReplaceRuntimeImportedContent(owner.Values, view.Document);
            }
            if (preview != null)
            { if (modelPreview != null) RetireEditorPreview(modelPreview); modelPreview = preview; preview = null; assetPreviewPath = previewEntry!.Path; }
            if (importedContentOwners.Remove(request.Entry.Id, out var previous))
            { previous.RetireFrame = frames + 2; retiredImportContentOwners.Add(previous); }
            importedContentOwners[request.Entry.Id] = owner; bundle = null; cache = null;
            if (request.Roots.Any(id => workspace.Session.FindAsset(id)?.Asset is Stride.Assets.Materials.MaterialAsset))
                InvalidateMaterialCompiledDependencies(owner.Bundle);
        }
        finally
        {
            if (preview != null) RetireEditorPreview(preview);
            if (bundle != null && cache != null
                && (candidateOwner != null && Volatile.Read(ref candidateOwner.PreviewLeases) != 0 || SceneViewsUseImportedContent(values.Select(value => value.Value).ToArray())))
            {
                // A failed compensation must retain resources still referenced by the engine.
                var retained = candidateOwner ?? new ImportContentOwner { Bundle = bundle, Cache = cache, Values = values.ToArray(), AssetIds = request.Roots };
                retained.RetireFrame = frames + 2;
                retiredImportContentOwners.Add(retained);
                bundle = null; cache = null;
            }
            if (cache != null) foreach (var value in values) cache.Unload(value.Value);
            bundle?.Dispose();
        }
    }

    private void StopImportSettingsUpdate()
    {
        var task = importSettingsBuild; var cancellation = importSettingsCancellation;
        importSettingsBuild = null; importSettingsCancellation = null; importCompilationRequest = null; cancellation?.Cancel();
        if (task == null) cancellation?.Dispose();
        else
        {
            Interlocked.Increment(ref detachedImportBuilds);
            _ = task.ContinueWith(completed =>
            {
                try { if (completed.Status == TaskStatus.RanToCompletion) CompiledImportAssetBuild.ReleaseOutput(completed.Result);
                    else if (completed.IsFaulted) _ = completed.Exception; }
                catch (Exception error) { outputLog.Append("Project", LogMessageType.Error, "Asset compilation", error.Message, error.ToString()); }
                finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedImportBuilds); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        // Call after viewport/preview disposal: owners are still borrowed until those consumers release them.
        foreach (var owner in importedContentOwners.Values)
        { owner.RetireFrame = frames + 2; retiredImportContentOwners.Add(owner); }
        importedContentOwners.Clear(); installedImportRevisions.Clear(); attemptedImportRevisions.Clear();
        pendingImportRevisions.Clear(); importCaptureErrors.Clear(); importSettingsWorkspace = null; importSettingsCheckAt = default;
        importPollingCache.Clear();
    }

    private void ClearImportedContentOwners()
    {
        StopImportSettingsUpdate();
        foreach (var owner in retiredImportContentOwners.ToArray())
        {
            if (Volatile.Read(ref owner.PreviewLeases) != 0 || owner.Preview is { IsDisposed: false } || MaterialPreviewUsesContent(owner.Bundle)
                || SceneViewsUseImportedContent(owner.Values.Select(value => value.Value).ToArray()))
                throw new InvalidOperationException("Imported content still has an active engine consumer.");
            owner.Dispose(); retiredImportContentOwners.Remove(owner);
        }
    }
}
