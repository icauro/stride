using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Real retained numeric widget drafts driving the native project command and compiler.</summary>
internal sealed class EngineActiveProjectPendingUiCapabilityProbe : EngineUiCapabilityProbe
{
    private ActiveProjectPendingParityRun? run;
    private object? completedEvidence;
    private int scrollPause;
    protected override string Scenario => "project.pending-switch";
    protected override double PhaseTimeoutSeconds => 120;
    protected override object Evidence => new { native = completedEvidence,
        currentDraft = run == null || run.Retired ? null : Game.InspectActiveProjectPendingDraft(run),
        checks = run?.Checks.ToArray(),
        scope = "Synthetic ImGui numeric input, actual structural command validation and native compiler cancellation; physical input and stale-target branches are separate." };
    internal object CaptureEvidence() => Evidence;
    internal CompiledProjectContent? ReturnedContent => run?.ReturnedContent;
    internal EngineActiveProjectPendingUiCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }

    protected override void Step()
    {
        if (scrollPause > 0) { scrollPause--; return; }
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (!Game.CanBeginActiveProjectPendingParity || !EnsureScenePresented()) break;
                run = Game.BeginActiveProjectPendingParityVerification(); Next(); break;
            case 1: if (ClickCounter()) Next(); break;
            case 2: SelectAll(true); Next(); break;
            case 3:
                SelectAll(false);
                foreach (var rune in "not-a-number".EnumerateRunes()) io.AddInputCharacter((uint)rune.Value);
                Next(); break;
            case 4:
                if (!Game.ActiveProjectPendingDraftReady(run!, "not-a-number")) break;
                Game.VerifyInvalidActiveProjectPendingSwitch(run!);
                io.AddKeyEvent(ImGuiKey.Escape, true); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 6:
                if (!Game.ActiveProjectPendingDraftCancelled(run!)) break;
                if (ClickCounter()) Next(); break;
            case 7: SelectAll(true); Next(); break;
            case 8:
                SelectAll(false);
                foreach (var rune in run!.After.ToString(System.Globalization.CultureInfo.InvariantCulture).EnumerateRunes())
                    io.AddInputCharacter((uint)rune.Value);
                Next(); break;
            case 9:
                if (!Game.ActiveProjectPendingDraftReady(run!, run!.After.ToString(System.Globalization.CultureInfo.InvariantCulture))) break;
                Game.SubmitValidActiveProjectPendingSwitch(run); Next(); break;
            case 10:
                if (Game.StepCancelActiveProjectPendingCompiler(run!)) Next(); break;
            case 11:
                completedEvidence = Game.StepActiveProjectPendingReturn(run!);
                if (completedEvidence == null) break;
                Finish(true, "project.pending-switch PASS: real invalid/valid native numeric drafts, actual compiler cancellation/output retirement and native return/Undo.");
                break;
        }
    }

    private bool ClickCounter()
    {
        var control = run!.Control;
        if (Game.Controls.Centers.ContainsKey(control)) return Click(control);
        var header = "component/" + run.Field.Component.Id + "/header";
        if (Game.Controls.Centers.ContainsKey(header)
            && (!Game.Controls.Expanded.TryGetValue(header, out var open) || !open))
        { Click(header); return false; }
        if (Game.InspectWorkflowToolBody("editor.properties") is not { } body || !body.Selected)
        {
            if (Game.InspectDockTabCenter("editor.properties") is { } tab)
            { Game.Controls.Centers["pending-switch/properties-tab"] = tab; Click("pending-switch/properties-tab"); }
            return false;
        }
        Gui.GetIO().AddMousePosEvent(body.Center.X, body.Center.Y); Gui.GetIO().AddMouseWheelEvent(0, -1);
        scrollPause = 2; return false;
    }

    private static void SelectAll(bool down)
    { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
    protected override void CleanInput()
    {
        SelectAll(false); Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Gui.GetIO().AddKeyEvent(ImGuiKey.Escape, false);
        // The Game retains the exact run; normal completion pumps genuine
        // producers, owned draft lease cancellation and guarded Undo before close.
    }
}
