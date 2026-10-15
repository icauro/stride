using Hexa.NET.ImGui;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool platformProgressPending, platformProgressKeepOpen, platformProgressFailed, platformProgressCancelled;
    private long platformProgressStarted;
    private string platformProgressMessage = "", platformProgressText = "";
    private readonly List<string> platformProgressMessages = [];
    private void BeginPackagePlatformProgress()
    {
        platformProgressMessages.Clear(); platformProgressText = platformProgressMessage = "";
        platformProgressKeepOpen = platformProgressFailed = platformProgressCancelled = false;
        platformProgressStarted = System.Diagnostics.Stopwatch.GetTimestamp(); platformProgressPending = true;
    }
    private void AppendPackagePlatformProgress(ProjectLoadState state)
    {
        platformProgressMessage = state.Detail;
        if (state.OutputLine != null) { platformProgressMessages.Add(state.OutputLine); platformProgressText = string.Join(Environment.NewLine, platformProgressMessages); }
    }
    private void CompletePackagePlatformProgress(IEnumerable<ILogMessage>? messages, Exception? failure, bool cancelled)
    {
        if (messages != null) foreach (var message in messages)
        {
            platformProgressMessages.Add(message.ToString() ?? message.Text);
            if (message.Type >= LogMessageType.Warning) platformProgressKeepOpen = true;
            if (message.Type >= LogMessageType.Error) platformProgressFailed = true;
        }
        if (failure != null)
        { platformProgressMessages.Add(failure.ToString()); platformProgressKeepOpen = platformProgressFailed = true; }
        platformProgressCancelled = cancelled;
        platformProgressText = string.Join(Environment.NewLine, platformProgressMessages);
    }
    private void DrawPackagePlatformProgress()
    {
        const string title = "Updating package..."; // Native PackageViewModel work-progress title.
        var running = IsPackagePlatformUpdating;
        if (platformProgressPending)
        {
            if (!running && !platformProgressKeepOpen) { platformProgressPending = false; return; }
            if (running && System.Diagnostics.Stopwatch.GetElapsedTime(platformProgressStarted).TotalMilliseconds < 500) return;
            Gui.OpenPopup(title); platformProgressPending = false;
        }
        Gui.SetNextWindowSize(UiScale(640, 480), ImGuiCond.Appearing);
        if (!Gui.BeginPopupModal(title, ImGuiWindowFlags.None)) return;
        try
        {
            if (!running && !platformProgressKeepOpen) { Gui.CloseCurrentPopup(); return; }
            DrawSelectableText("##PlatformUpdateMessage", running ? platformProgressMessage : platformProgressCancelled ? "Operation cancelled."
                : platformProgressFailed ? "Operation failed." : "Operation completed.");
            Gui.ProgressBar(running ? -(float)Gui.GetTime() : 1f, new Vector2(-1, UiScale(25)), "");
            var height = Math.Max(Gui.GetFrameHeight(), Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing());
            if (Gui.BeginChild("##PlatformUpdateLog", new Vector2(0, height), ImGuiChildFlags.Borders)) DrawSelectableText("##PlatformUpdateLogText", platformProgressText);
            ImGuiScopes.EndChild();
            Gui.BeginDisabled(running);
            if (DrawNativeButton(EditorLocalization.Widget("Close", "Button"))) { platformProgressKeepOpen = false; Gui.CloseCurrentPopup(); }
            Gui.EndDisabled();
            // The stock Update platforms work-progress window is not cancellable.
        }
        finally { Gui.EndPopup(); }
    }
}
