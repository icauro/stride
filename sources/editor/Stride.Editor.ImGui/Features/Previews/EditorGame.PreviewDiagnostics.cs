using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static void AccumulatePreviewBuildLevel(ref int highest, ProjectLoadState state)
    {
        if (state.Phase == "Building project") return;
        var level = state.AssetDiagnostic?.Severity ?? state.Diagnostic?.Level ?? (state.OutputLine is { } text ? BuildOutputLevel(text) : LogMessageType.Info);
        var observed = Volatile.Read(ref highest);
        while ((int)level > observed)
        {
            var actual = Interlocked.CompareExchange(ref highest, (int)level, observed);
            if (actual == observed) break;
            observed = actual;
        }
    }
    private T? CreateAssetPreview<T>(AssetEntry asset, Func<T> create) where T : class
    {
        if (projectContent == null) return null;
        try { return create(); }
        catch (Exception error)
        {
            if (RoutePreviewGraphicsFailure(error)) return null;
            outputLog.Append("Project", LogMessageType.Error, "Asset preview", error.Message, error.ToString(), assetId: asset.Id);
            statusMessage = error.Message;
            return null;
        }
    }
    private void RecordAssetPreviewRenderFailure(string module, Exception error)
    {
        if (RoutePreviewGraphicsFailure(error)) return;
        var asset = workspace?.Assets.FirstOrDefault(candidate => string.Equals(candidate.Path, assetPreviewPath, StringComparison.OrdinalIgnoreCase));
        outputLog.Append("Project", LogMessageType.Error, module, error.Message, error.ToString(), assetId: asset?.Id);
    }
    private void RecordAssetPreviewBuildFailure(Stride.Core.Assets.AssetId id, Exception error, AssetThumbnailFailure failure)
    {
        if (RoutePreviewGraphicsFailure(error)) return;
        SetAssetThumbnailBuildStatus(id, new(LogMessageType.Error, failure));
        outputLog.Append("Project", LogMessageType.Error, "Asset preview", error.Message, error.ToString(), assetId: id);
    }
    private bool RoutePreviewGraphicsFailure(Exception error)
    {
        try { EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error); return false; }
        catch (Stride.Graphics.GraphicsDeviceException failure) when (failure.Status != Stride.Graphics.GraphicsDeviceStatus.Normal)
        {
            if (!IsEditorGraphicsRecovering) throw;
            // The actual failed-tick recovery already owns this device. Cleanup
            // keeps its owner for retry; no authored-asset error is published.
            return true;
        }
    }
}
