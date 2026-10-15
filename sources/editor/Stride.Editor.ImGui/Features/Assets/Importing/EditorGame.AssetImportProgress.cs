using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string assetImportProgressTitle = "Add asset…";
    private bool assetImportProgressPending, assetImportProgressFailed, assetImportProgressCancelled;
    private bool assetImportProgressKeepOpen;
    private readonly List<string> assetImportProgressMessages = [];
    private string assetImportProgressLogText = "";
    private long assetImportProgressStarted;

    private void BeginAssetImportProgress(bool reimport)
    {
        assetImportProgressRetired = false;
        assetImportProgressTitle = reimport ? "Update assets from source" : "Add asset…";
        assetImportProgressMessages.Clear(); assetImportProgressFailed = assetImportProgressCancelled = false;
        assetImportProgressLogText = "";
        assetImportProgressKeepOpen = false;
        assetImportProgressStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        assetImportProgressPending = true;
    }

    private void CompleteAssetImportProgress(IEnumerable<ILogMessage>? messages, Exception? error, bool cancelled)
    {
        assetImportProgressCancelled = cancelled;
        if (messages != null)
            foreach (var message in messages)
            {
                assetImportProgressMessages.Add(message.ToString() ?? message.Text);
                if (message.Type >= LogMessageType.Warning) assetImportProgressKeepOpen = true;
                if (message.Type >= LogMessageType.Error) assetImportProgressFailed = true;
            }
        if (error != null) { assetImportProgressFailed = assetImportProgressKeepOpen = true; assetImportProgressMessages.Add(error.ToString()); }
        assetImportProgressLogText = string.Join(Environment.NewLine, assetImportProgressMessages);
    }

    private void DrawAssetImportProgress()
    {
        var running = assetImportTask != null || assetDropSourcesTask != null || factorySourceTask != null || resourceSourceCopyTask != null;
        var keepOpen = assetImportProgressKeepOpen;
        if (assetImportProgressPending)
        {
            if (!running && !keepOpen) { assetImportProgressPending = false; assetImportProgressRetired = true; return; }
            // The reference waits 500 ms before opening its work-progress window.
            if (running && System.Diagnostics.Stopwatch.GetElapsedTime(assetImportProgressStarted).TotalMilliseconds < 500) return;
            Gui.OpenPopup(assetImportProgressTitle); assetImportProgressPending = false;
        }
        Gui.SetNextWindowSize(UiScale(640, 480), ImGuiCond.Appearing);
        using var popup = ImGuiScopes.Modal(assetImportProgressTitle, ImGuiWindowFlags.None);
        if (!popup.DrawContents) { if (!running && !keepOpen) assetImportProgressRetired = true; return; }
        if (!running && !keepOpen) { Gui.CloseCurrentPopup(); assetImportProgressRetired = true; return; }
        var progress = AssetImportProgress;
        var message = running ? progress?.Message ?? "" : assetImportProgressCancelled ? "Operation cancelled."
            : assetImportProgressFailed ? "Operation failed." : "Operation completed.";
        DrawSelectableText("##AssetImportMessage", message);
        var fraction = -(float)Gui.GetTime();
        if (progress is { FileCount: > 0 })
        {
            var step = progress.Completed ? 1f : progress.HasKnownSteps && progress.StepCount > 0
                ? Math.Clamp((float)progress.CurrentStep / progress.StepCount, 0, 1) : 0f;
            fraction = Math.Clamp((progress.FileIndex + step) / progress.FileCount, 0, 1);
        }
        Gui.ProgressBar(running ? fraction : 1, new System.Numerics.Vector2(-1, UiScale(25)), "");
        var height = Math.Max(Gui.GetFrameHeight(), Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing());
        if (Gui.BeginChild("##AssetImportLog", new System.Numerics.Vector2(0, height), ImGuiChildFlags.Borders))
            DrawSelectableText("##AssetImportLogText", assetImportProgressLogText);
        ImGuiScopes.EndChild();
        using (ImGuiScopes.Disabled(running))
            if (Gui.Button(EditorLocalization.Widget("Close", "Button"))) { assetImportProgressMessages.Clear(); assetImportProgressRetired = true; Gui.CloseCurrentPopup(); }
        if (running)
        {
            Gui.SameLine();
            using (ImGuiScopes.Disabled(assetImportCancellation?.IsCancellationRequested == true || assetDropSourcesCancellation?.IsCancellationRequested == true || factorySourceCancellation?.IsCancellationRequested == true || resourceSourceCopyCancellation?.IsCancellationRequested == true))
                if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) CancelAssetImport();
        }
    }
}
