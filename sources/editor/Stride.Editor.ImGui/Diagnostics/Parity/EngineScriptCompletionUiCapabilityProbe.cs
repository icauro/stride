using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineScriptCompletionUiCapabilityProbe(EditorGame game, string artifacts)
    : EngineUiCapabilityProbe(game, artifacts)
{
    private string path = "";
    private int shortcutFrame, completionFrame, historyFrame;
    private bool nativeShortcutDown;
    private long saveBaseline;
    private object? finalEvidence;
    protected override string Scenario => "stock-dispatch.completion-ui";
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new { path, shortcutFrame, nativeShortcutDown, completionFrame, historyFrame,
        saveBaseline, finalEvidence, current = Game.CaptureScriptCompletionUiEvidence(),
        coverage = "Actual synthetic Ctrl+Space, native provider completion Selectable, canonical source commit, native toolbar history/Save and independent persisted bytes. Text and caret setup are fixture arrangement; no desktop input, other completion policies or code actions." };
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: path = Game.PrepareScriptCompletionUiVerification(); Next(); break;
            case 1: if (Game.PrepareScriptCompletionUiCaret()) Next(); break;
            case 2:
                if (!Game.ScriptCompletionUiReady || !Game.VerifyRecoveryAcceptanceNativeTab(path, out _)) break;
                shortcutFrame = Gui.GetFrameCount(); io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.Space, true); Next(); break;
            case 3:
                if (Gui.GetFrameCount() <= shortcutFrame || !io.KeyCtrl || !Gui.IsKeyDown(ImGuiKey.Space)) break;
                nativeShortcutDown = true; io.AddKeyEvent(ImGuiKey.Space, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 4:
                if (Gui.IsKeyDown(ImGuiKey.Space) || io.KeyCtrl) break;
                if (!Game.Controls.Centers.ContainsKey("script/completion/CoverageWidgetCompletion")) break;
                if (Click("script/completion/CoverageWidgetCompletion")) { completionFrame = Gui.GetFrameCount(); Next(); }
                break;
            case 5:
                if (!Game.ScriptCompletionUiAccepted(completionFrame)) break;
                if (!Game.ScriptCompletionUiState(EditorGame.CompletionUiCommittedText, 2, true)) break;
                Require(nativeShortcutDown, "Completion popup must follow actual native Ctrl+Space processing");
                if (!Game.StockHistoryToolbarReady("undo", path)) break;
                if (Click("toolbar/undo")) { historyFrame = Gui.GetFrameCount(); Next(); } break;
            case 6:
                if (!Game.StockHistoryToolbarAccepted("undo", path, historyFrame)
                    || !Game.ScriptCompletionUiState(EditorGame.CompletionUiText, 1, true)) break;
                if (!Game.StockHistoryToolbarReady("redo", path)) break;
                if (Click("toolbar/redo")) { historyFrame = Gui.GetFrameCount(); Next(); } break;
            case 7:
                if (!Game.StockHistoryToolbarAccepted("redo", path, historyFrame)
                    || !Game.ScriptCompletionUiState(EditorGame.CompletionUiCommittedText, 2, true)) break;
                if (!Game.AssetTemplateSaveUiReceipt.Eligible) break;
                var commitSaveBaseline = Game.AssetTemplateSaveUiReceipt.Accepted;
                if (Click("toolbar/save")) { saveBaseline = commitSaveBaseline; Next(); } break;
            case 8:
                if (Game.AssetTemplateSaveUiReceipt.Accepted <= saveBaseline
                    || !Game.ScriptCompletionUiSaved(EditorGame.CompletionUiCommittedText)) break;
                if (!Game.StockHistoryToolbarReady("undo", path)) break;
                if (Click("toolbar/undo")) { historyFrame = Gui.GetFrameCount(); Next(); } break;
            case 9:
                if (!Game.StockHistoryToolbarAccepted("undo", path, historyFrame)
                    || !Game.ScriptCompletionUiState(EditorGame.CompletionUiText, 1, false)) break;
                if (!Game.StockHistoryToolbarReady("undo", path)) break;
                if (Click("toolbar/undo")) { historyFrame = Gui.GetFrameCount(); Next(); } break;
            case 10:
                if (!Game.StockHistoryToolbarAccepted("undo", path, historyFrame)
                    || !Game.ScriptCompletionUiState(Game.ScriptCompletionUiOriginalText, 0, false)) break;
                if (!Game.AssetTemplateSaveUiReceipt.Eligible) break;
                var restoreSaveBaseline = Game.AssetTemplateSaveUiReceipt.Accepted;
                if (Click("toolbar/save")) { saveBaseline = restoreSaveBaseline; Next(); } break;
            case 11:
                if (Game.AssetTemplateSaveUiReceipt.Accepted <= saveBaseline
                    || !Game.ScriptCompletionUiSaved(Game.ScriptCompletionUiOriginalText)
                    || !Game.ScriptCompletionUiState(Game.ScriptCompletionUiOriginalText, 0, true)) break;
                finalEvidence = Game.CaptureScriptCompletionUiEvidence();
                Game.CompleteScriptCompletionUiVerification();
                Finish(true, "Native completion popup click committed one canonical journal; actual toolbar Undo/Redo/Save and exact original baseline restoration passed."); break;
        }
    }
    protected override void CleanInput()
    {
        Gui.GetIO().AddKeyEvent(ImGuiKey.Space, false); Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, false);
        Game.CancelScriptCompletionUiVerification();
    }
}
