using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Texture? workflowRenderedCapture;
    private int workflowRenderedCaptureFrame;
    private bool workflowRenderedCaptureSaved;

    // Called after the actual editor backbuffer is rendered, before teardown.
    // Only explicitly requested UI test captures retain this GPU image.
    internal void RetainWorkflowRenderedCapture(Texture backBuffer)
    {
        if (workflowTest == null || workflowRenderedCaptureSaved || DiagnosticCompletionActive || IsEditorGpuRetirementFinalizing
            || Gui.GetDrawData().TotalVtxCount == 0 || !args.Contains("--capture")) return;
        // Keep the prior valid frame across a resize rather than disposing a
        // texture with a pending copy. The capture sidecar records its frame.
        if (workflowRenderedCapture != null && (workflowRenderedCapture.Width != backBuffer.Width
            || workflowRenderedCapture.Height != backBuffer.Height || workflowRenderedCapture.Format != backBuffer.Format)) return;
        if (workflowRenderedCapture == null)
        {
            workflowRenderedCapture = Texture.New2D(GraphicsDevice, backBuffer.Width, backBuffer.Height,
                backBuffer.Format, TextureFlags.ShaderResource);
        }
        GraphicsContext.CommandList.Copy(backBuffer, workflowRenderedCapture);
        workflowRenderedCaptureFrame = Gui.GetFrameCount();
    }

    internal void SaveWorkflowRenderedCapture(Texture backBuffer)
    {
        // Completion may become inactive again in the terminal callback. Its
        // final draw must not overwrite the already saved editor frame.
        if (workflowRenderedCaptureSaved) return;
        var index = Array.IndexOf(args, "--capture");
        if (index < 0 || index + 1 >= args.Length) return;
        workflowRenderedCaptureSaved = true;
        var path = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
            (workflowRenderedCapture ?? backBuffer).Save(GraphicsContext.CommandList, stream, ImageFileType.Png);
        File.WriteAllText(Path.ChangeExtension(path, ".capture.json"), System.Text.Json.JsonSerializer.Serialize(new
        { path, source = workflowRenderedCapture == null ? "current-rendered-backbuffer" : "last-rendered-editor-frame",
            guiFrame = workflowRenderedCapture == null ? Gui.GetFrameCount() : workflowRenderedCaptureFrame }));
        workflowRenderedCapture?.Dispose(); workflowRenderedCapture = null;
    }
}
