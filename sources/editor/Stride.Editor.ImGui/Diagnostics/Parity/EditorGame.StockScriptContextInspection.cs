using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal unsafe object InspectStockScriptContext()
    {
        var context = Gui.GetCurrentContext();
        var popups = new List<object>();
        for (var index = 0; index < context.OpenPopupStack.Size; index++)
        {
            var popup = context.OpenPopupStack[index];
            var window = popup.Window;
            popups.Add(new { index, popup.PopupId, windowId = window == null ? 0u : window->ID,
                name = window == null ? null : Marshal.PtrToStringUTF8((nint)window->Name),
                modal = window != null && (window->Flags & ImGuiWindowFlags.Modal) != 0,
                viewportId = window == null ? 0u : window->ViewportId });
        }
        return new
        {
            frame = Gui.GetFrameCount(), popups,
            knownControls = Controls.Centers.Keys.Where(key => key.StartsWith("script-reload/", StringComparison.Ordinal)
                || key.StartsWith("external-change/", StringComparison.Ordinal) || key.StartsWith("document-close/", StringComparison.Ordinal)
                || key.StartsWith("close/", StringComparison.Ordinal)).Select(key => new { key,
                    nativeId = Controls.NativeIDs.GetValueOrDefault(key), frame = Controls.NativeFrames.GetValueOrDefault(key) }).ToArray(),
            sourceReloadPromptOpen, sourceReloadRevision, sourcePromptRevision, acceptedSourceReloadRevision,
            pendingSourceReloadProjects = pendingSourceReloadProjects.ToArray(), recompiling = scriptRecompileTask != null,
            closePromptOpen, closeRequested, closeError, documentCloseError,
            retainedSources = authoring?.RetainedStates.Where(state => state.Document is ScriptSourceDocument or ProjectBuildSettingsDocument)
                .Select(state => new { state.Document.Path, type = state.Document.GetType().Name,
                    state.IsPublished, state.IsDisposed, state.Generation, state.Document.IsDirty }).ToArray()
        };
    }
}
