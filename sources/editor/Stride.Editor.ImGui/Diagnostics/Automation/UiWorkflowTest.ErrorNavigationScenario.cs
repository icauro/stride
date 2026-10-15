using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private BuildDiagnostic? expectedOutputDiagnostic;
    private bool outputExpandedForDiagnostic;
    private System.Numerics.Vector2 outputSplitterDestination;
    private float outputHeightBeforeDrag;

    private void UpdateErrorNavigationScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 11300:
                if (!outputExpandedForDiagnostic && game.InspectWorkflowOutputSplitter() is { } splitter)
                {
                    io.AddMousePosEvent(splitter.Start.X, splitter.Start.Y);
                    outputHeightBeforeDrag = game.WorkflowOutputHeight();
                    RecordAction("splitter-aim", "Output", splitter.Start.X, splitter.Start.Y, io.MousePos.X, io.MousePos.Y);
                    outputSplitterDestination = splitter.End; stage = 11340; break;
                }
                if (ClickToolControl("output/channel/Build", "editor.output")) Next(); break;
            case 11340: io.AddMouseButtonEvent(0, true); Next(); break;
            case 11341: io.AddMousePosEvent(outputSplitterDestination.X, outputSplitterDestination.Y); Next(); break;
            case 11342: io.AddMouseButtonEvent(0, false); stage = 11343; wait = 2; break;
            case 11343:
                Assert(game.WorkflowOutputHeight() > outputHeightBeforeDrag + 1,
                    "Actual Output splitter drag must enlarge its native window before log navigation: before="
                    + outputHeightBeforeDrag + "; after=" + game.WorkflowOutputHeight());
                outputExpandedForDiagnostic = true; stage = 11300; break;
            case 11301: if (ClickToolControl("output/search", "editor.output")) Next(); break;
            case 11302: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11303:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in CompileFailureMarker) io.AddInputCharacter(character);
                Next(); break;
            case 11304: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11305: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11306:
                var output = game.InspectOutputWorkflow();
                if (output.Updating || output.Search != CompileFailureMarker || output.MatchCount == 0) break;
                if (ClickToolControl("output/find-next", "editor.output")) Next(); break;
            case 11307:
                if (ClickActualOutputDiagnostic()) Next(); break;
            case 11308:
                if (ClickActualOutputDiagnostic()) Next(); break;
            case 11309:
                if (game.InspectWorkflowActiveScriptCaret() is not { } caret || expectedOutputDiagnostic == null) break;
                Assert(SameAssetPath(caret.Path, expectedOutputDiagnostic.Path)
                    && caret.Line == Math.Max(1, expectedOutputDiagnostic.Line)
                    && caret.Column == Math.Max(1, expectedOutputDiagnostic.Column),
                    "Double-clicking the actual rendered diagnostic must navigate the canonical source caret to its real file/line/column");
                Assert(game.CaptureInspectionSnapshot().Documents.Any(document => SameAssetPath(document.Path, expectedOutputDiagnostic.Path)),
                    "Error navigation must retain the canonical source document");
                File.WriteAllBytes(failedLoadSource!, failedLoadSourceBackup!);
                checks.Add("Actual native diagnostic glyph double-click opens canonical source at compiler file/line/column");
                CompleteIndependentSubset(); break;
        }
    }

    private bool ClickActualOutputDiagnostic()
    {
        if (game.InspectWorkflowOutputDiagnostic(CompileFailureMarker) is not { } diagnostic) return false;
        if (diagnostic.Point is not { } point)
        {
            // The log body may be below the parent panel's controls at small
            // dock heights. Scroll the actual output body to reveal its glyphs.
            ClickToolControl("output/actual-diagnostic", "editor.output");
            return false;
        }
        expectedOutputDiagnostic = diagnostic.Diagnostic;
        game.Controls.Centers["output/actual-diagnostic"] = point;
        return Click("output/actual-diagnostic");
    }
}
