using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<ScriptSourceDocument, ScriptTextEditor> scriptEditors = [];
    private ImFontPtr scriptFont;
    private int fallbackScriptEditorFontSize = 12;
    private unsafe void InitializeScriptEditorFont()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var path = Path.Combine(directory, "consola.ttf");
        if (!File.Exists(path)) path = Path.Combine(directory, "cour.ttf");
        if (File.Exists(path))
        {
            scriptFont = Gui.GetIO().Fonts.AddFontFromFileTTF(path, 12);
            EditorFontSetup.MergeFallbacks(scriptFont, 12, "Consolas");
        }
    }
    private unsafe void DrawScriptDocument(ScriptSourceDocument document)
    {
        if (IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing || IsEditorGraphicsRecovering || scriptGlyphImages?.IsDraining == true)
        { if (scriptEditors.TryGetValue(document, out var suspended)) suspended.SuspendSourcePresentation(); return; }
        if (!scriptEditors.TryGetValue(document, out var editor))
            scriptEditors.Add(document, editor = new ScriptTextEditor());
        editor.CanEditPresentation = CaptureScriptPresentationAuthority(document);
        editor.NativeGlyphImages = GetScriptGlyphImages();
        editor.NativeGlyphCommands = GraphicsContext.CommandList;
        editor.NativeMenuItem = DrawNativeMenuItem;
        editor.AssistanceControlRecorder = Controls.Enabled ? (id, pressed) => RecordScriptAssistanceUiControl(document, id, pressed) : null;
        editor.CanTransferNativeText = () => CanTransferScriptText;
        editor.NativeModalTick = TickScriptOleFeedback;
        editor.ObserveCodeActionContext(scriptSemantics, scriptSemantics?.Epoch ?? -1, sessionHistory?.Stack.NextUndoTransaction?.Id, sessionHistory?.Stack.NextRedoTransaction?.Id);
        var fontSize = preferences?.Current.ScriptEditorFontSize ?? fallbackScriptEditorFontSize;
        var io = Gui.GetIO();
        var origin = Gui.GetCursorScreenPos();
        if (io.KeyCtrl && Gui.IsWindowHovered() && Gui.IsMouseHoveringRect(origin, origin + Gui.GetContentRegionAvail()))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.MouseWheelY, Gui.GetID("ScriptEditorFontSize"));
            if (io.MouseWheel != 0)
            {
                var next = fontSize + (io.MouseWheel > 0 ? 1 : -1);
                if (next is >= 8 and <= 72)
                {
                    if (preferences == null) fallbackScriptEditorFontSize = fontSize = next;
                    else if (preferences.TryUpdate(current => current.ScriptEditorFontSize = next)) fontSize = next;
                }
            }
        }
        var hasFont = scriptFont.Handle != null;
        Gui.PushFont(hasFont ? scriptFont : Gui.GetFont(), UiScale(fontSize));
        try { editor.Draw(document); }
        finally { Gui.PopFont(); }
        if (scriptSemanticResult is { } result) editor.ApplySemanticResult(result);
        HandleScriptCodeAssistance(document, editor);
        if (editor.InputActive && Gui.GetIO().KeyCtrl)
        {
            if (editor.MayEditSource(document) && Gui.IsKeyPressed(ImGuiKey.Z, false)) Execute(() => { if (Gui.GetIO().KeyShift) document.Redo(); else document.Undo(); });
            else if (editor.MayEditSource(document) && Gui.IsKeyPressed(ImGuiKey.Y, false)) Execute(document.Redo);
            else if (Gui.IsKeyPressed(ImGuiKey.S, false)) Execute(SaveAllDocuments);
        }
    }
    private void ReleaseScriptEditor(ScriptSourceDocument document)
    {
        if (!scriptEditors.TryGetValue(document, out var editor)) return;
        editor.Dispose(); scriptEditors.Remove(document);
    }
    private void ClearScriptEditors()
    {
        StopScriptSemantics();
        foreach (var editor in scriptEditors.Values) editor.Dispose();
        scriptEditors.Clear();
    }
}
