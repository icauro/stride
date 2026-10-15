using System.Security.Cryptography;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record CubemapRequest(EngineViewport Viewport, EditorSessionHistory History, ProjectWorkspace Workspace,
        EditorAuthoringState Owner, long Revision, string Output, byte[]? Hash);
    private CubemapRequest? pendingCubemapRequest;
    private void QueueSceneCubemapCapture()
    {
        if (IsAuthoringPipelineUpdating || engineViewport?.PresentedDocument is not { } current || workspace == null || SessionAuthoringHistory is not { } history
            || history.Registry.Find(current.Asset.Id) is not { IsPublished: true } owner || !ReferenceEquals(owner.Document, current)) return;
        var output = WindowsFileDialogs.Save(Window.NativeWindow.Handle, EditorLocalization.Text("Save"), "DDS texture\0*.dds\0\0", Path.GetDirectoryName(workspace.OpenPath), "dds");
        if (output == null) return;
        byte[]? hash = null;
        if (File.Exists(output)) { using var stream = File.Open(output, FileMode.Open, FileAccess.Read, FileShare.Read); hash = SHA256.HashData(stream); }
        pendingCubemapRequest = new(engineViewport, history, workspace, owner, current.EditRevision, output, hash);
    }
    private void PumpSceneCubemapCapture()
    {
        if (pendingCubemapRequest is not { } request) return;
        pendingCubemapRequest = null;
        if (IsAuthoringPipelineUpdatingExceptSceneLightProbe || IsEditorGraphicsRecovering || closeRequested || closePromptOpen || closingDocument != null || !request.Viewport.Visible
            || !ReferenceEquals(engineViewport, request.Viewport) || !ReferenceEquals(workspace, request.Workspace)
            || !ReferenceEquals(SessionAuthoringHistory, request.History) || !request.Owner.IsPublished || request.Owner.IsDisposed
            || request.Owner.Document is not EntityHierarchyDocument current || current.EditRevision != request.Revision
            || !ReferenceEquals(request.History.Registry.Find(current.Asset.Id), request.Owner)) return;
        ExecuteSceneCapture(() =>
        {
            using var image = request.Viewport.CaptureSceneCubemap();
            using var output = new MemoryStream(); image.Save(output, ImageFileType.Dds);
            WindowsOwnedAssetFileWriter.Write(request.Output, request.Hash, output.ToArray());
        });
    }
}
