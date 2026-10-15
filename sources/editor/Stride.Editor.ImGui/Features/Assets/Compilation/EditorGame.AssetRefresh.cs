using Stride.Core.Serialization.Contents;
using Stride.Assets;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal (string? Url, bool CastsShadows) RuntimeAssetMaterial(string url) => engineViewport!.RuntimeModelMaterial(url);
    private Task<string>? assetPreviewBuild;
    private CancellationTokenSource? assetPreviewCancellation;
    private sealed class AssetPreviewBuildProgress
    {
        internal ProjectLoadState? State;
        internal int Level = (int)Stride.Core.Diagnostics.LogMessageType.Info;
    }
    private AssetPreviewBuildProgress? assetPreviewProgress;
    private long assetPreviewCleanupRetryAt;
    private string? assetPreviewCleanupFailure;
    private string? updatingAssetPath;
    private Stride.Core.Mathematics.Vector2? updatingPreviewAngles;
    internal bool IsAssetPreviewUpdating => assetPreviewBuild != null;
    internal int? AssetPreviewBuildProcessId
    {
        get
        {
            var progress = Volatile.Read(ref assetPreviewProgress);
            return progress == null ? null : Volatile.Read(ref progress.State)?.ProcessId;
        }
    }
    internal float RuntimeAssetRadius(string url) => engineViewport!.RuntimeModelHalfExtent(url);
    internal Stride.Core.Mathematics.Vector3 RuntimeAssetSize(string url) => engineViewport!.RuntimeModelSize(url);
    private void StartAssetPreviewUpdate()
    {
        if (assetPreviewBuild != null || projectLoading != null || IsAuthoringPipelineUpdating || IsUserProjectBuilding) throw new InvalidOperationException("A build is already running.");
        SaveAssetDocument(); updatingAssetPath = assetDocument!.Path;
        updatingPreviewAngles = PreviewAngles(assetDocument.Asset.Type);
        assetPreviewCancellation = new CancellationTokenSource(); var token = assetPreviewCancellation.Token;
        var package = workspace!.BuildProjectPath;
        assetPreviewCleanupRetryAt = 0; assetPreviewCleanupFailure = null;
        var progress = new AssetPreviewBuildProgress();
        assetPreviewProgress = progress;
        assetPreviewBuild = Task.Run(() => CompiledProjectBuild.Build(package, token, state =>
        {
            Volatile.Write(ref progress.State, state);
            AccumulatePreviewBuildLevel(ref progress.Level, state);
            RecordBuildOutput(state);
        }), token);
        RecordEngineAssetUpdateStarted(assetPreviewBuild, updatingAssetPath);
        statusMessage = "Updating asset preview...";
    }
    private void PumpAssetPreviewUpdate()
    {
        PumpRetiredRefreshedModels();
        if (assetPreviewBuild?.IsCompleted != true) return;
        var task = assetPreviewBuild;
        var cancelled = assetPreviewCancellation!.IsCancellationRequested;
        if (cancelled || task.IsCanceled)
        {
            if (!TryCleanupEditorOwnedWork(() =>
            {
                if (task.Status == TaskStatus.RanToCompletion) OwnedProjectBuildOutput.Release(task.Result);
                else if (task.IsFaulted) _ = task.Exception;
            }, ref assetPreviewCleanupRetryAt, ref assetPreviewCleanupFailure, "Preview build cleanup")) return;
            assetPreviewBuild = null; assetPreviewProgress = null;
            assetPreviewCancellation.Dispose(); assetPreviewCancellation = null;
            statusMessage = "Preview update cancelled; saved asset retained"; return;
        }
        assetPreviewBuild = null;
        var completedProgress = assetPreviewProgress; assetPreviewProgress = null;
        assetPreviewCancellation.Dispose(); assetPreviewCancellation = null;
        Execute(() =>
        {
            string output;
            try { output = task.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                var failed = workspace?.Assets.FirstOrDefault(entry => string.Equals(entry.Path, updatingAssetPath, StringComparison.OrdinalIgnoreCase));
                if (failed != null) RecordAssetPreviewBuildFailure(failed.Id, error, AssetThumbnailFailure.Broken);
                throw;
            }
            AssetEntry entry; string url;
            try
            {
                entry = workspace!.Assets.Single(entry => string.Equals(entry.Path, updatingAssetPath, StringComparison.OrdinalIgnoreCase));
                url = workspace.AssetItems.Single(item => string.Equals(item.FullPath.ToOSPath(), entry.Path, StringComparison.OrdinalIgnoreCase)).Location.ToString();
            }
            catch (Exception failure)
            {
                try { OwnedProjectBuildOutput.Release(output); }
                catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
                throw;
            }
            CompiledProjectContent? bundle = null; ContentManager? cache = null; Model? model = null; ModelAssetPreview? preview = null;
            try
            {
                bundle = new CompiledProjectContent(this, output, () => OwnedProjectBuildOutput.Release(output)); cache = bundle.CreateObjectCache(); model = cache.Load<Model>(url, ContentManagerLoaderSettings.StreamingDisabled);
                var settings = workspace.FindAsset(entry.Id).Package.GetGameSettingsAssetOrDefault();
                var mode = settings.GetOrCreate<EditorSettings>().RenderingMode;
                var colorSpace = settings.GetOrCreate<RenderingSettings>().ColorSpace;
                preview = new ModelAssetPreview(this, renderer!, bundle, url, mode == RenderingMode.HDR, true, updatingPreviewAngles, colorSpace);
                foreach (var view in SnapshotSceneResourceViews())
                    view.Viewport.ReplaceRuntimeModel(url, model);
                var previous = refreshedModels.Where(resource => resource.Url == url).ToArray();
                refreshedModels.Add((bundle, cache, model, url)); bundle = null; cache = null; model = null;
                InvalidateAssetThumbnails(entry.Id);
                InvalidateMaterialCompiledDependencies(refreshedModels[^1].Bundle, reapplyDirtyTextures: true);
                SetAssetThumbnailBuildStatus(entry.Id,
                    new((Stride.Core.Diagnostics.LogMessageType)(completedProgress == null ? (int)Stride.Core.Diagnostics.LogMessageType.Info : Volatile.Read(ref completedProgress.Level)), AssetThumbnailFailure.None));
                var old = modelPreview; modelPreview = preview; preview = null;
                if (texturePreview != null) { RetireEditorPreview(texturePreview); texturePreview = null; }
                if (old != null) RetireEditorPreview(old);
                if (materialPreview != null) { RetireEditorPreview(materialPreview); materialPreview = null; }
                foreach (var resource in previous) { refreshedModels.Remove(resource); retiredModels.Add((resource.Bundle, resource.Cache, resource.Model, frames + 2)); }
                selectedAssetPath = entry.Path; assetPreviewPath = entry.Path;
                focusAssetPreview = true; showAssetPreview = true; statusMessage = "Asset preview updated";
            }
            catch (Exception error) { RecordAssetPreviewBuildFailure(entry.Id, error, AssetThumbnailFailure.Broken); throw; }
            finally
            {
                if (preview != null) RetireEditorPreview(preview);
                if (bundle != null)
                {
                    if (model != null) retiredModels.Add((bundle, cache!, model, frames + 2));
                    else RetireEditorPreview(bundle);
                }
            }
        });
    }

}
