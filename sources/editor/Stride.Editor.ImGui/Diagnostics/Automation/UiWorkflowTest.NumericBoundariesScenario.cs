using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private static readonly (string Text, ulong Expected)[] NumericBoundaryInputs =
    [
        ("9007199254740993", 9007199254740993UL),
        ("18446744073709551615", ulong.MaxValue),
        ("18446744073709551614", ulong.MaxValue - 1),
        ("0", 0UL), ("1", 1UL),
        ("-1", 0UL), ("18446744073709551616", ulong.MaxValue),
        ("+18446744073709551615", ulong.MaxValue),
        ("+0", 0UL), ("-0", 0UL),
    ];
    private int numericBoundaryRound;
    private ulong numericBoundaryBefore;
    private long numericBoundaryHistory;
    private string numericBoundaryTransactionsBefore = "";
    private string NumericBoundaryTransactions => string.Join(" | ", ((EditorSessionHistory)game.VerificationHistoryOwner!).History.Entries
        .Select(entry => entry.Id + ":" + entry.Name));
    private Guid numericBoundaryOwner, numericBoundaryComponent;
    private NumericDraftFeedbackInspection? numericFeedbackBefore, numericFeedbackInvalid;
    private EditorOutputEntry[] numericUnrelatedOutput = [];
    private static readonly (string Action, uint Expected)[] NumericFlagInputs =
    [
        ("bit/Group31", 0x80000001U), ("all", uint.MaxValue),
        ("all", uint.MaxValue), ("none", 0U), ("invert", uint.MaxValue),
        ("bit/Group31", 0x7fffffffU), ("invert", 0x80000000U),
    ];
    private int numericFlagRound;
    private int numericFlagOpenRetries;
    private uint numericFlagBefore;
    private ComponentField NumericBoundaryField => game.Document!.GetComponentFields(
        game.Document.Selected!.Components.Single(component => component.GetType().Name == "InspectorTypes"))
        .Single(member => member.Name == "Counter64");
    private string NumericBoundaryControl => "component/" + NumericBoundaryField.Component.Id + "/Counter64";
    private ComponentField NumericFlagField => game.Document!.GetComponentFields(
        game.Document.Selected!.Components.Single(component => component.GetType().Name == "InspectorTypes"))
        .Single(member => member.Name == "Mask");
    private string NumericFlagControl => "component/" + NumericFlagField.Component.Id + "/Mask/flags";

    private void AssertNumericFlags(uint expected)
    {
        Assert(Convert.ToUInt32(NumericFlagField.Node.Retrieve()) == expected
            && game.ReadRuntimeComponentValueVerification(NumericFlagField) is { } runtime
            && Convert.ToUInt32(runtime) == expected,
            "Flags authored/runtime must retain exact Group31 and independent declared bits: " + expected);
    }

    private void AssertNumericBoundary(ulong value)
    {
        var field = NumericBoundaryField;
        Assert(field.Node.Retrieve() is ulong actual && actual == value
            && game.ReadRuntimeComponentValueVerification(field) is ulong runtime && runtime == value,
            "Counter64 authored and real runtime values must retain exact unsigned-64 bits: " + value);
    }

    private void UpdateNumericBoundariesScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 13300:
                if (Click("entity/Sphere")) Next(); break;
            case 13301:
                if (game.Document?.Selected?.Name != "Sphere" || !PrepareReferenceScript()) break;
                var field = NumericBoundaryField;
                Assert(field.Type == typeof(ulong), "Fixture Counter64 must be the actual ulong template");
                Assert(!game.Document.IsDirty, "Numeric fixture must begin at its native saved baseline");
                numericBoundaryOwner = field.Component.Entity.Id;
                numericBoundaryComponent = field.Component.Id;
                Next(); break;
            case 13302:
                if (!PrepareReferenceScript()) break;
                numericBoundaryBefore = (ulong)NumericBoundaryField.Node.Retrieve()!;
                numericBoundaryHistory = game.Document!.History.Position;
                numericBoundaryTransactionsBefore = NumericBoundaryTransactions;
                Assert(!game.Document.IsDirty, "Each numeric boundary round starts at an actual save point");
                if (ClickInspectorControl(NumericBoundaryControl)) Next(); break;
            case 13303:
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 13304:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in NumericBoundaryInputs[numericBoundaryRound].Text) io.AddInputCharacter(character);
                Next(); break;
            case 13305: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 13306: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 13307:
                if (Click("entity/Sphere")) Next(); break;
            case 13308:
                var expected = NumericBoundaryInputs[numericBoundaryRound].Expected;
                AssertNumericBoundary(expected);
                var changed = expected != numericBoundaryBefore;
                Assert(game.Document!.History.Position == numericBoundaryHistory + (changed ? 1 : 0)
                    && game.Document.IsDirty == changed,
                    "Exact/coerced unsigned input must publish one gesture; no-op must preserve history/savepoint: round=" + numericBoundaryRound
                    + ", before=" + numericBoundaryBefore + ", expected=" + expected + ", baselineHistory=" + numericBoundaryHistory
                    + ", actualHistory=" + game.Document.History.Position + ", dirty=" + game.Document.IsDirty + ", changed=" + changed
                    + ", baselineTransactions=" + numericBoundaryTransactionsBefore + ", actualTransactions=" + NumericBoundaryTransactions);
                if (!changed) { stage = 13313; wait = 2; break; }
                if (Click("toolbar/undo")) Next(); break;
            case 13309:
                AssertNumericBoundary(numericBoundaryBefore);
                Assert(game.Document!.History.Position == numericBoundaryHistory && !game.Document.IsDirty,
                    "Unsigned gesture undo must restore exact prior bits and saved baseline");
                if (Click("toolbar/redo")) Next(); break;
            case 13310:
                AssertNumericBoundary(NumericBoundaryInputs[numericBoundaryRound].Expected);
                if (Click("toolbar/save")) Next(); break;
            case 13311:
                if (game.Document!.IsDirty) break;
                Next(); break;
            case 13312:
                var disk = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset;
                var script = disk.Hierarchy.Parts[numericBoundaryOwner].Entity.Components.Single(component => component.Id == numericBoundaryComponent);
                Assert(script.GetType().GetProperty("Counter64")!.GetValue(script) is ulong persisted
                    && persisted == NumericBoundaryInputs[numericBoundaryRound].Expected,
                    "Independent native YAML reload must retain full unsigned-64 value and actual component identity");
                Next(); break;
            case 13313:
                numericBoundaryRound++;
                if (numericBoundaryRound < NumericBoundaryInputs.Length) { stage = 13302; wait = 2; }
                else { stage = 13320; wait = 2; }
                break;
            case 13320:
                numericBoundaryBefore = (ulong)NumericBoundaryField.Node.Retrieve()!;
                numericBoundaryHistory = game.Document!.History.Position;
                numericFeedbackBefore = game.InspectNumericDraftFeedback(NumericBoundaryField);
                numericUnrelatedOutput = game.CaptureWorkflowRecentOutput(128);
                if (ClickInspectorControl(NumericBoundaryControl)) Next(); break;
            case 13321: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 13322:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "not-a-number") io.AddInputCharacter(character);
                Next(); break;
            case 13323: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 13324: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 13325:
                AssertNumericBoundary(numericBoundaryBefore);
                Assert(game.Document!.History.Position == numericBoundaryHistory && !game.Document.IsDirty,
                    "Invalid syntactic input must leave authored/runtime/history/savepoint unchanged");
                numericFeedbackInvalid = game.InspectNumericDraftFeedback(NumericBoundaryField);
                Assert(numericFeedbackInvalid.ControlId != null && !string.IsNullOrEmpty(numericFeedbackInvalid.DraftError)
                    && numericFeedbackInvalid.ComponentError == numericFeedbackInvalid.DraftError,
                    "Invalid Counter64 input must own a real native draft/control and its exact component validation diagnostic");
                if (ClickInspectorControl(NumericBoundaryControl)) Next(); break;
            case 13326: io.AddKeyEvent(ImGuiKey.Escape, true); Next(); break;
            case 13327: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 13328:
                if (Click("entity/Sphere")) Next(); break;
            case 13329:
                AssertNumericBoundary(numericBoundaryBefore);
                Assert(game.Document!.History.Position == numericBoundaryHistory && !game.Document.IsDirty,
                    "Cancel invalid unsigned draft must retain actual native baseline");
                var canceledFeedback = game.InspectNumericDraftFeedback(NumericBoundaryField);
                Assert(canceledFeedback.ControlId == numericFeedbackInvalid!.ControlId
                    && canceledFeedback.DraftError == null && canceledFeedback.ComponentError == null
                    && !canceledFeedback.ConversionFailure
                    && canceledFeedback.Status != numericFeedbackInvalid.DraftError,
                    "Escape must clear only the canceled Counter64 draft, native conversion feedback and matching field/status diagnostic");
                Assert(canceledFeedback.OtherComponentErrors.SequenceEqual(numericFeedbackBefore!.OtherComponentErrors),
                    "Counter64 cancellation must retain unrelated component diagnostics");
                var retainedOutput = game.CaptureWorkflowRecentOutput(512);
                Assert(numericUnrelatedOutput.All(entry => retainedOutput.Any(current => current.Sequence == entry.Sequence && current == entry)),
                    "Canceling a field draft must retain the actual unrelated native compiler/output diagnostics");
                if (ClickInspectorControl(NumericBoundaryControl))
                {
                    checks.Add("Invalid Counter64 draft/control error ownership and Escape cleanup; unrelated component/output records retained");
                    Next();
                }
                break;
            case 13330: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 13331:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "12345678901234567890") io.AddInputCharacter(character);
                Next(); break;
            case 13332: io.AddKeyEvent(ImGuiKey.Escape, true); Next(); break;
            case 13333: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 13334:
                if (Click("entity/Sphere")) Next(); break;
            case 13335:
                AssertNumericBoundary(numericBoundaryBefore);
                Assert(game.Document!.History.Position == numericBoundaryHistory && !game.Document.IsDirty,
                    "Escape of a valid unsigned draft must cancel without publishing a transaction");
                checks.Add("Exact ulong >2^53, max/min/adjacent endpoints, signed zero, native negative/overflow coercion, no-op history, undo/redo, native runtime and independent persistence; invalid/canceled drafts preserve state");
                Next(); stage = 13340; break;
            case 13340:
                if (!PrepareReferenceScript()) break;
                if (numericFlagRound == 0)
                    Assert(Convert.ToUInt32(NumericFlagField.Node.Retrieve()) == 1U,
                        "The actual RenderGroupMask fixture must begin with Group0");
                numericFlagBefore = Convert.ToUInt32(NumericFlagField.Node.Retrieve());
                numericBoundaryHistory = game.Document!.History.Position;
                Assert(!game.Document.IsDirty, "Flags rounds start at the native saved baseline");
                if (ClickInspectorControl(NumericFlagControl)) Next(); break;
            case 13341:
                if (game.InspectWorkflowPopupBody() == null
                    || !game.Controls.Centers.Keys.Any(id => id.StartsWith(NumericFlagControl + "/", StringComparison.Ordinal)))
                {
                    Assert(++numericFlagOpenRetries <= 3,
                        "Actual Mask flags combo must open its native popup and publish its own visible item geometry");
                    stage = 13340; wait = 2; break;
                }
                if (ClickPopupControl(NumericFlagControl + "/" + NumericFlagInputs[numericFlagRound].Action)) Next();
                break;
            case 13342:
                AssertNumericFlags(NumericFlagInputs[numericFlagRound].Expected);
                var flagChanged = numericFlagBefore != NumericFlagInputs[numericFlagRound].Expected;
                Assert(game.Document!.History.Position == numericBoundaryHistory + (flagChanged ? 1 : 0)
                    && game.Document.IsDirty == flagChanged,
                    "Individual/bulk native flags must publish one transaction; repeated All remains a no-op");
                io.AddKeyEvent(ImGuiKey.Escape, true); Next(); break;
            case 13343: io.AddKeyEvent(ImGuiKey.Escape, false); Next(); break;
            case 13344:
                if (numericFlagBefore == NumericFlagInputs[numericFlagRound].Expected) { stage = 13350; wait = 2; break; }
                if (Click("toolbar/undo")) Next(); break;
            case 13345:
                AssertNumericFlags(numericFlagBefore);
                Assert(!game.Document!.IsDirty && game.Document.History.Position == numericBoundaryHistory,
                    "Flags undo restores exact independent bit state and savepoint");
                if (Click("toolbar/redo")) Next(); break;
            case 13346:
                AssertNumericFlags(NumericFlagInputs[numericFlagRound].Expected);
                if (Click("toolbar/save")) Next(); break;
            case 13347:
                if (game.Document!.IsDirty) break;
                Next(); break;
            case 13348:
                var flagDisk = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset;
                var flagScript = flagDisk.Hierarchy.Parts[numericBoundaryOwner].Entity.Components.Single(component => component.Id == numericBoundaryComponent);
                Assert(Convert.ToUInt32(flagScript.GetType().GetProperty("Mask")!.GetValue(flagScript)) == NumericFlagInputs[numericFlagRound].Expected
                    && flagScript.GetType().GetProperty("Counter64")!.GetValue(flagScript) is ulong savedCounter && savedCounter == numericBoundaryBefore,
                    "Independent YAML must preserve flags and the unrelated unsigned field by native component ID");
                stage = 13350; wait = 2; break;
            case 13350:
                numericFlagRound++;
                numericFlagOpenRetries = 0;
                if (numericFlagRound < NumericFlagInputs.Length) { stage = 13340; wait = 2; break; }
                checks.Add("Actual RenderGroupMask Group31, individual checkbox, All/None/Invert, repeated-All no-op, runtime/history/savepoint and independent native persistence");
                CompleteIndependentSubset(); break;
        }
    }
}

internal sealed record NumericDraftFeedbackInspection(uint? ControlId, string? DraftError,
    bool ConversionFailure, string? ComponentError, string Status, string[] OtherComponentErrors);

internal sealed partial class EditorGame
{
    // Read-only actor diagnostic. Reads the real retained draft/control, never
    // validates, commits, cancels, creates a draft or clears any product state.
    internal NumericDraftFeedbackInspection InspectNumericDraftFeedback(ComponentField field)
    {
        const System.Reflection.BindingFlags privateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var table = typeof(PropertyStringEditor).GetField("drafts", privateInstance)!.GetValue(PropertyGrid.Strings)!;
        object?[] arguments = [field.Node, null];
        var found = (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!;
        uint? controlId = null;
        string? draftError = null;
        if (found && arguments[1] is System.Collections.IDictionary values)
        {
            if (values.Count != 1)
                throw new InvalidOperationException("Numeric diagnostic requires one actual control draft for its canonical node.");
            controlId = values.Keys.Cast<uint>().Single();
            var draft = values[controlId.Value]!;
            draftError = (string?)draft.GetType().GetField("LastValidationError", privateInstance)!.GetValue(draft);
        }
        var failures = (IReadOnlyDictionary<uint, long>)typeof(PropertyConversionFeedback)
            .GetField("failures", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var same = componentErrors.Where(entry => ReferenceEquals(entry.Key.Node, field.Node)).ToArray();
        if (same.Length > 1) throw new InvalidOperationException("Multiple component diagnostic owners match the actual numeric node.");
        var other = componentErrors.Where(entry => !ReferenceEquals(entry.Key.Node, field.Node))
            .Select(entry => entry.Key.Component.Id + "/" + entry.Key.Name + ":" + entry.Value)
            .Order(StringComparer.Ordinal).ToArray();
        return new(controlId, draftError, controlId is { } id && failures.ContainsKey(id),
            same.SingleOrDefault().Value, statusMessage, other);
    }
}
