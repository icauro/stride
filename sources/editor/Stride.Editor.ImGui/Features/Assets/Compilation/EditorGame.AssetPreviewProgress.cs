using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAssetPreviewUpdate()
    {
        if (assetPreviewBuild == null) return;
        Gui.SetNextWindowSize(UiScale(620, 200), Hexa.NET.ImGui.ImGuiCond.Appearing);
        using var window = ImGuiScopes.Window("Updating asset preview", Hexa.NET.ImGui.ImGuiWindowFlags.NoCollapse);
        if (!window.DrawContents) return;
        var progress = Volatile.Read(ref assetPreviewProgress);
        var state = progress == null ? null : Volatile.Read(ref progress.State);
        DrawNativeLabel(state?.Phase ?? "Preparing build");
        Gui.PushTextWrapPos(0);
        try { DrawNativeLabel(state?.Detail ?? "Compiling asset dependencies..."); }
        finally { Gui.PopTextWrapPos(); }
        using var disabled = ImGuiScopes.Disabled(assetPreviewCancellation == null || assetPreviewCancellation.IsCancellationRequested);
        if (DrawNativeButton("Cancel update")) assetPreviewCancellation?.Cancel();
        if (Controls.Enabled) Controls.Record("asset-update/cancel");
    }
}
