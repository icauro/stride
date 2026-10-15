using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record CompositorBuildRequest(ProjectWorkspace Workspace, AssetEntry Entry, AssetMetadataDocument Document, MediaCompilationSnapshot Snapshot,
        SceneCompositorReloadScope? SceneReload = null);
    private sealed record CompositorContentOwner(AssetId Id, CompiledProjectContent Bundle, string Revision);
    private readonly Dictionary<AssetId, CompositorContentOwner> compositorContentOwners = [];
    private readonly List<(CompiledProjectContent Bundle, int Frame)> retiredCompositorContent = [];
    private readonly Dictionary<AssetId, string> attemptedCompositorRevisions = [];
    private readonly Dictionary<AssetId, (string Revision, DateTime Since)> pendingCompositorRevisions = [];
    private readonly Dictionary<AssetId, string> compositorCaptureErrors = [];
    private readonly AuthoredImportPollingCache compositorPollingCache = new();
    private ProjectWorkspace? compositorBuildWorkspace;
    private Task<string>? compositorBuild;
    private CancellationTokenSource? compositorCancellation;
    private CompositorBuildRequest? compositorBuildRequest;
    private DateTime compositorCheckAt;
    private int detachedCompositorBuilds, compositorBuildLevel;
    private bool IsCompositorSettingsUpdating => compositorBuild != null || Volatile.Read(ref detachedCompositorBuilds) != 0;

    private bool IsSceneCompositor(AssetId id)
    {
        if (workspace == null) return false;
        var settings = EditorSceneSettings.ResolveCurrentGameSettings(workspace, sessionHistory, out _);
        return AttachedReferenceManager.GetAttachedReference(settings.GraphicsCompositor)?.Id == id;
    }
    private MediaCompilationSnapshot CaptureCompositorCompilation(AssetEntry entry, bool validate)
    {
        var shaders = workspace!.AssetItems.Where(item => item.Asset is EffectShaderAsset).Select(item => item.Id).ToArray();
        return CaptureAuthoredPreviewCompilation(entry, validate, shaders, excludeCompositorLayout: true);
    }
    private void PumpCompositorSettingsUpdate()
    {
        for (var index = retiredCompositorContent.Count - 1; index >= 0; index--)
        {
            var retired = retiredCompositorContent[index];
            if (frames < retired.Frame || retiredEditorPreviews.Any(item => item.Preview is RetiredEngineViewport)) continue;
            RetireEditorPreview(retired.Bundle); retiredCompositorContent.RemoveAt(index);
        }
        if (!ReferenceEquals(compositorBuildWorkspace, workspace))
        { StopCompositorSettingsUpdate(); compositorBuildWorkspace = workspace; compositorPollingCache.Clear(); attemptedCompositorRevisions.Clear(); pendingCompositorRevisions.Clear(); compositorCaptureErrors.Clear(); }
        if (compositorBuild is { IsCompleted: true } completed)
        {
            var request = compositorBuildRequest!; var cancelled = compositorCancellation!.IsCancellationRequested;
            compositorBuild = null; compositorBuildRequest = null; compositorCancellation.Dispose(); compositorCancellation = null;
            string? output = null;
            try
            {
                output = completed.GetAwaiter().GetResult();
                if (!cancelled && ReferenceEquals(workspace, request.Workspace)
                    && (request.SceneReload == null || IsCurrentSceneCompositorReload(request.SceneReload))
                    && documents.AuthoredDocuments.Any(owner => ReferenceEquals(owner, request.Document))
                    && workspace!.Assets.Any(entry => entry.Id == request.Entry.Id)
                    && !request.Document.HasCompositorSettingChange && CaptureCompositorCompilation(request.Entry, true).Revision == request.Snapshot.Revision)
                {
                    var errors = request.Document.ValidateCompositorGraph(IsSceneCompositor(request.Entry.Id));
                    if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                    var ownedOutput = output;
                    var bundle = new CompiledProjectContent(this, ownedOutput, () => CompiledMediaBuild.ReleaseOutput(ownedOutput)); output = null;
                    var nativeReloadSucceeded = true;
                    try
                    {
                        if (request.SceneReload is { } reload && IsCurrentSceneCompositorReload(reload))
                            nativeReloadSucceeded = RecreateSceneCompositorViewports(reload, bundle);
                        if (compositorContentOwners.Remove(request.Entry.Id, out var previous)) retiredCompositorContent.Add((previous.Bundle, frames + 2));
                        compositorContentOwners[request.Entry.Id] = new(request.Entry.Id, bundle, request.Snapshot.Revision);
                    }
                    catch { bundle.Dispose(); throw; }
                    if (nativeReloadSucceeded)
                        SetAssetThumbnailBuildStatus(request.Entry.Id, new((LogMessageType)Volatile.Read(ref compositorBuildLevel), AssetThumbnailFailure.None));
                    InvalidateCompiledAssetThumbnails(request.Entry.Id);
                    compositorCaptureErrors.Remove(request.Entry.Id);
                }
            }
            catch (OperationCanceledException) when (cancelled) { }
            catch (Exception error)
            {
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                if (!cancelled && ReferenceEquals(workspace, request.Workspace)) RecordAssetPreviewBuildFailure(request.Entry.Id, error, AssetThumbnailFailure.Broken);
            }
            finally { if (output != null) CompiledMediaBuild.ReleaseOutput(output); }
        }
        if (compositorBuildRequest?.SceneReload is { } activeReload && !IsCurrentSceneCompositorReload(activeReload)) compositorCancellation?.Cancel();
        if (workspace == null || DateTime.UtcNow < compositorCheckAt) return;
        compositorCheckAt = DateTime.UtcNow.AddMilliseconds(250);
        var sceneReloadOwner = PrepareSceneCompositorReload(out var sceneReload);
        var documentsToCheck = documents.AuthoredDocuments.OfType<AssetMetadataDocument>().Where(owner => owner.CompositorAsset != null).ToArray();
        if (documentsToCheck.Length == 0) return;
        var inputs = AuthoredImportPollingCache.CaptureInputs(workspace, authoring);
        foreach (var owner in documentsToCheck)
        {
            if (owner.HasCompositorSettingChange) continue;
            var entry = workspace.Assets.FirstOrDefault(entry => entry.Id == owner.AuthoredAsset.Id); if (entry == null) continue;
            try
            {
                var captured = compositorPollingCache.Capture(workspace, authoring, entry, inputs, _ => CaptureCompositorCompilation(entry, false)).Snapshot;
                var id = entry.Id;
                var ownerSceneReload = ReferenceEquals(owner, sceneReloadOwner) ? sceneReload : null;
                var requiresSceneReload = ownerSceneReload != null && RequiresSceneCompositorReload(ownerSceneReload);
                if (compositorBuildRequest?.Entry.Id == id && captured.Revision != compositorBuildRequest.Snapshot.Revision) compositorCancellation?.Cancel();
                if (compositorBuild != null) continue;
                if (!requiresSceneReload && !owner.IsDirty && !compositorContentOwners.ContainsKey(id) && !attemptedCompositorRevisions.ContainsKey(id))
                { attemptedCompositorRevisions[id] = captured.Revision; continue; }
                if (requiresSceneReload)
                {
                    if (IsSameSceneCompositorReloadAttempt(sceneReload!, captured.Revision)) continue;
                }
                else if (compositorContentOwners.GetValueOrDefault(id)?.Revision == captured.Revision || attemptedCompositorRevisions.GetValueOrDefault(id) == captured.Revision) continue;
                if (!pendingCompositorRevisions.TryGetValue(id, out var pending) || pending.Revision != captured.Revision)
                { pendingCompositorRevisions[id] = (captured.Revision, DateTime.UtcNow); continue; }
                if ((DateTime.UtcNow - pending.Since).TotalMilliseconds < 180 || !CanStartAuthoringPreviewUpdate) continue;
                var errors = owner.ValidateCompositorGraph(IsSceneCompositor(id));
                attemptedCompositorRevisions[id] = captured.Revision;
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                captured = CaptureCompositorCompilation(entry, true); attemptedCompositorRevisions[id] = captured.Revision;
                if (ownerSceneReload != null)
                {
                    // Native validation may commit legitimate pending scene values.
                    // Capture the requesting views after that commit, before worker dispatch.
                    var currentOwner = PrepareSceneCompositorReload(out var currentScope);
                    ownerSceneReload = ReferenceEquals(currentOwner, owner) ? currentScope : null;
                    requiresSceneReload = ownerSceneReload != null && RequiresSceneCompositorReload(ownerSceneReload);
                }
                if (requiresSceneReload)
                {
                    if (!IsCurrentSceneCompositorReload(ownerSceneReload!)) continue;
                    RememberSceneCompositorReloadAttempt(ownerSceneReload!, captured.Revision);
                    if (compositorContentOwners.GetValueOrDefault(id) is { } installed && installed.Revision == captured.Revision)
                    {
                        RecreateSceneCompositorViewports(ownerSceneReload!, installed.Bundle, onlyMismatched: true);
                        compositorCaptureErrors.Remove(id);
                        break;
                    }
                }
                compositorBuildRequest = new(workspace, entry, owner, captured, ownerSceneReload); compositorBuildLevel = (int)LogMessageType.Info;
                compositorCancellation = new(); var token = compositorCancellation.Token;
                compositorBuild = Task.Run(() => CompiledMediaBuild.Build(captured, token, state =>
                { if (!token.IsCancellationRequested || state.CompilerEvent != CompilerRunEvent.None) { AccumulatePreviewBuildLevel(ref compositorBuildLevel, state); RecordBuildOutput(state); } }), token);
                break;
            }
            catch (Exception error)
            {
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                if (compositorCaptureErrors.GetValueOrDefault(entry.Id) != error.ToString())
                { compositorCaptureErrors[entry.Id] = error.ToString(); RecordAssetPreviewBuildFailure(entry.Id, error, AssetThumbnailFailure.Broken); }
            }
        }
    }
    private void StopCompositorSettingsUpdate()
    {
        var task = compositorBuild; var cancellation = compositorCancellation;
        compositorBuild = null; compositorBuildRequest = null; compositorCancellation = null; cancellation?.Cancel();
        if (task == null) cancellation?.Dispose();
        else
        {
            Interlocked.Increment(ref detachedCompositorBuilds);
            _ = task.ContinueWith(completed =>
            {
                try { if (completed.Status == TaskStatus.RanToCompletion) CompiledMediaBuild.ReleaseOutput(completed.Result); else if (completed.IsFaulted) _ = completed.Exception; }
                finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedCompositorBuilds); }
            }, TaskScheduler.Default);
        }
    }
    private void ClearCompiledCompositors()
    {
        StopCompositorSettingsUpdate();
        foreach (var owner in compositorContentOwners.Values) owner.Bundle.Dispose(); compositorContentOwners.Clear();
        foreach (var retired in retiredCompositorContent) retired.Bundle.Dispose(); retiredCompositorContent.Clear();
        compositorPollingCache.Clear(); attemptedCompositorRevisions.Clear(); pendingCompositorRevisions.Clear(); compositorCaptureErrors.Clear(); compositorBuildWorkspace = null;
        sceneCompositorReloadAttempt = null;
        sceneCompositorSettingsFailure = null;
    }
    private void ReleaseCompositorDocument(IEditorDocument document)
    {
        if (document is not AssetMetadataDocument { CompositorAsset: not null } owner) return;
        selectionHistory.UnregisterCompositor(owner.AuthoredAsset.Id);
        compositorEditors.Remove(owner);
        owner.ClearCompositorPresentationRoots();
        var id = owner.AuthoredAsset.Id;
        if (ReferenceEquals(compositorBuildRequest?.Document, owner)) StopCompositorSettingsUpdate();
        pendingCompositorRevisions.Remove(id); compositorCaptureErrors.Remove(id);
    }
}
