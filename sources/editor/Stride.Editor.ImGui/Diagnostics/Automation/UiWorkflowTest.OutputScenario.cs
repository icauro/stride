using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private string outputBaseline = "";
    private int outputPreviousMatch;
    private string outputSearchToken = "";
    private bool outputBuildRequested;

    private void UpdateOutputScenario()
    {
        var io = Gui.GetIO();
        var state = game.InspectOutputWorkflow();
        switch (stage)
        {
            case 11100:
                if (ClickToolControl("output/channel/Build", "editor.output")) Next(); break;
            case 11101:
                if (state.Channel != "Build" || state.Updating || !state.InfoEnabled) break;
                if (state.VisibleText.Length == 0)
                {
                    // Cached project loading may produce no compiler log. Ask
                    // the actual existing Build shortcut to produce one rather
                    // than inserting synthetic diagnostic messages.
                    if (!outputBuildRequested && game.CanRequestOutputWorkflowBuild)
                    { stage = 11120; wait = 2; }
                    break;
                }
                outputBaseline = state.VisibleText;
                var token = System.Text.RegularExpressions.Regex.Matches(outputBaseline, "[A-Za-z]{5,}")
                    .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value).FirstOrDefault();
                Assert(token != null, "Actual compiler log must contain a searchable token");
                outputSearchToken = token!;
                if (ClickToolControl("output/level/Info", "editor.output")) Next(); break;
            case 11102:
                if (state.Updating || state.InfoEnabled) break;
                Assert(!state.VisibleText.Contains("Info:", StringComparison.Ordinal)
                    && state.VisibleText != outputBaseline, "Disabling Info must actually remove compiler Info messages from displayed output");
                if (ClickToolControl("output/level/Info", "editor.output")) Next(); break;
            case 11103:
                if (state.Updating || !state.InfoEnabled) break;
                Assert(state.VisibleText.Contains(outputSearchToken, StringComparison.OrdinalIgnoreCase),
                    "Restoring Info must restore the actual compiler-log token");
                if (ClickToolControl("output/search", "editor.output")) Next(); break;
            case 11104: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11105:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in outputSearchToken) io.AddInputCharacter(character);
                Next(); break;
            case 11106: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11107: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11108:
                if (state.Updating || state.Search != outputSearchToken || state.MatchCount == 0) break;
                outputPreviousMatch = state.MatchIndex;
                if (ClickToolControl("output/find-next", "editor.output")) Next(); break;
            case 11109:
                Assert(state.MatchIndex == (outputPreviousMatch + 1) % state.MatchCount,
                    "Next search result must advance the actual output match selection");
                if (ClickToolControl("output/search", "editor.output")) Next(); break;
            case 11110: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11111: io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.Backspace, true); Next(); break;
            case 11112: io.AddKeyEvent(ImGuiKey.Backspace, false); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11113: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11114:
                if (state.Updating || state.Search.Length != 0) break;
                Assert(state.InfoEnabled && !game.Document!.IsDirty,
                    "Output filtering/search must restore filter state without mutating authored data");
                checks.Add("Actual compiler-log level filtering, committed search and next-match navigation through UI");
                CompleteIndependentSubset(); break;
            case 11120:
                io.AddKeyEvent(ImGuiKey.F6, true); outputBuildRequested = true; Next(); break;
            case 11121:
                io.AddKeyEvent(ImGuiKey.F6, false); stage = 11101; wait = 2; break;
        }
    }
}

internal sealed partial class EditorGame
{
    internal bool CanRequestOutputWorkflowBuild => CanStartUserProjectBuild && !IsAuthoringPipelineUpdating
        && projectLoading == null && assetPreviewBuild == null;
}
