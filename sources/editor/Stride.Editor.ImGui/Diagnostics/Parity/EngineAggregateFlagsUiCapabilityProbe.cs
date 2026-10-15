using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Actual mixed-owned flags widgets in one canonical scene, with native shared history and persistence.</summary>
internal sealed class EngineAggregateFlagsUiCapabilityProbe : EngineUiCapabilityProbe
{
    private static readonly (string Command, uint First, uint Second)[] Commands =
    [ ("bit/Group1", 3U, 0x80000002U), ("invert", 0xfffffffcU, 0x7ffffffdU),
      ("all", uint.MaxValue, uint.MaxValue), ("all", uint.MaxValue, uint.MaxValue),
      ("none", 0U, 0U), ("none", 0U, 0U), ("invert", uint.MaxValue, uint.MaxValue) ];
    private AggregateFlagsParityFixture? fixture;
    private object? diagnosticContract;
    private int round, completed, scrollPause;
    private long history;
    private uint beforeFirst = 1, beforeSecond = 0x80000000;
    private string control = "";
    protected override string Scenario => "E09.aggregate-flags";
    protected override object Evidence => new { completedCommands = completed, diagnosticContract,
        numericDraft = fixture == null ? null : Game.InspectAggregateFlagsNumericDraft(fixture),
        scope = "Two independently owned real InspectorTypes components in one canonical scene; inherited/cross-document flags are outside this case." };
    internal EngineAggregateFlagsUiCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        if (scrollPause > 0) { scrollPause--; return; }
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                Game.AssertCompiledSceneVerification(); fixture = Game.PrepareAggregateFlagsParityVerification(); Next(); break;
            case 3:
                if (!Game.VerifyAggregateFlagsParityValues(fixture!, beforeFirst, beforeSecond)) break;
                if (ClickInspector("component/" + fixture!.FirstComponent.Id + "/Counter64")) Next(); break;
            case 4: SelectAll(true); Next(); break;
            case 5: SelectAll(false); foreach (var rune in "not-a-number".EnumerateRunes()) io.AddInputCharacter((uint)rune.Value); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 7: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 8:
                if (Game.InspectNumericDraftFeedback(Game.AggregateFlagsNumericField(fixture!)).DraftError == null) break;
                diagnosticContract = Game.VerifyDiagnosticOwnershipParity(Game.AggregateFlagsNumericField(fixture!));
                SeekPhase(80); break;
            case 80:
                // Enter can deactivate InputText. Cancellation must travel
                // through the same real refocus/Escape route as numeric tests.
                if (ClickInspector("component/" + fixture!.FirstComponent.Id + "/Counter64")) Next(); break;
            case 81: io.AddKeyEvent(ImGuiKey.Escape, true); SeekPhase(9); break;
            case 9: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 10:
                Require(Game.InspectNumericDraftFeedback(Game.AggregateFlagsNumericField(fixture!)).DraftError == null,
                    "Actual Escape must retire the failed numeric draft after the diagnostic receipt contract.");
                Game.SelectAggregateFlagsParityVerification(fixture!); Next(); break;
            case 11:
                control = Game.AggregateFlagsControl(fixture!); history = fixture!.Owner.History.Position;
                if (ClickInspector(control)) Next(); break;
            case 12:
                if (ClickPopup(control + "/" + Commands[round].Command)) Next(); break;
            case 13:
                var command = Commands[round];
                if (!Game.VerifyAggregateFlagsParityValues(fixture!, command.First, command.Second)) break;
                var changed = beforeFirst != command.First || beforeSecond != command.Second;
                Require(fixture!.Owner.History.Position == history + (changed ? 1 : 0) && fixture.Owner.IsDirty == changed,
                    "Mixed flags must publish one shared history transaction; repeated equal owned bulk actions publish none.");
                Game.CertifyAggregateFlagsParityTransaction(fixture, history, changed);
                io.AddKeyEvent(ImGuiKey.Escape, true); Next(); break;
            case 14: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 15:
                if (beforeFirst == Commands[round].First && beforeSecond == Commands[round].Second) { SeekPhase(21); break; }
                if (Click("toolbar/undo")) Next(); break;
            case 16:
                if (!Game.VerifyAggregateFlagsParityValues(fixture!, beforeFirst, beforeSecond)) break;
                Require(fixture!.Owner.History.Position == history && !fixture.Owner.IsDirty,
                    "One Undo must restore both actual owners and the native savepoint.");
                if (Click("toolbar/redo")) Next(); break;
            case 17:
                if (!Game.VerifyAggregateFlagsParityValues(fixture!, Commands[round].First, Commands[round].Second)) break;
                if (Click("toolbar/save")) Next(); break;
            case 18: if (fixture!.Owner.IsDirty) break; Next(); break;
            case 19:
                Require(Game.VerifyAggregateFlagsParityValues(fixture!, Commands[round].First, Commands[round].Second, persisted: true),
                    "Independent native YAML reload must preserve both real component masks and unrelated Counter64.");
                SeekPhase(21); break;
            case 21:
                beforeFirst = Commands[round].First; beforeSecond = Commands[round].Second; completed++; round++;
                if (round < Commands.Length) SeekPhase(11);
                else Finish(true, "E09.aggregate-flags PASS: actual mixed flags controls, no-op bulk commands, shared undo/redo, runtime and native persistence; exact diagnostic receipt cancellation.");
                break;
        }
    }
    private bool ClickInspector(string id)
    {
        if (Game.Controls.Centers.ContainsKey(id)) return Click(id);
        if (Game.InspectWorkflowToolBody("editor.properties") is not { } body || !body.Selected)
        {
            if (Game.InspectDockTabCenter("editor.properties") is { } tab)
            { Game.Controls.Centers["aggregate-proof/properties-tab"] = tab; Click("aggregate-proof/properties-tab"); }
            return false;
        }
        Gui.GetIO().AddMousePosEvent(body.Center.X, body.Center.Y); Gui.GetIO().AddMouseWheelEvent(0, -1);
        scrollPause = 2; return false;
    }
    private bool ClickPopup(string id)
    {
        if (Game.Controls.Centers.ContainsKey(id)) return Click(id);
        if (Game.InspectWorkflowPopupBody() is not { } body) { SeekPhase(11); return false; }
        Gui.GetIO().AddMousePosEvent(body.X, body.Y); Gui.GetIO().AddMouseWheelEvent(0, -1);
        scrollPause = 2; return false;
    }
    private static void SelectAll(bool down)
    { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
    protected override void CleanInput()
    {
        SelectAll(false); Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Gui.GetIO().AddKeyEvent(ImGuiKey.Escape, false);
        if (fixture != null) Game.RestoreAggregateFlagsParityVerification(fixture);
    }
}
