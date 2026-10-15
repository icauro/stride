using System.Security.Cryptography;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Grouped actor over one already loaded private project; every proof mutation uses a real UI control.</summary>
internal sealed class EngineVisualScriptHistoryCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private string path = "", browserName = "";
    private VisualScriptHistoryNativeState? baseline;
    private object? baselineDescription;
    private Guid[] baselineAppliedTransactions = [], baselineRedoTransactions = [];
    private Guid[][] baselineAppliedParticipants = [];
    private string? firstOutput, secondOutput;
    private Guid[] firstPropertyIds = [], secondPropertyIds = [];
    private Guid[] ownedTransactions = [];
    private int historyStep;
    private string nativeAssetControl = "";
    private string[] browserLocations = [];
    private int browserLocationIndex;
    private readonly StockFolderNavigationAttempt folderNavigation = new();
    private int gestureAttempt, gestureIssuedFrame = -1, gestureBeforePosition;
    private double gestureDeadline;
    private bool gesturePrepared;
    private string gestureBeforeText = "";
    private Guid[] gestureBeforeTransactions = [], gestureBeforeProperties = [];
    private readonly List<object> checkpoints = [];
    private static readonly (string Control, int Properties)[] HistoryCommands =
    [
        ("toolbar/undo", 1), ("toolbar/undo", 0), ("toolbar/redo", 1), ("toolbar/redo", 2),
        ("toolbar/undo", 1), ("toolbar/undo", 0), ("toolbar/redo", 1), ("toolbar/redo", 2),
        ("toolbar/undo", 1), ("toolbar/undo", 0), ("toolbar/redo", 1), ("toolbar/redo", 2),
        ("toolbar/undo", 1), ("toolbar/undo", 0),
    ];
    protected override string Scenario => "visualscript.generated-history";
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new
    {
        path, historyStep, checkpoints, nativeAssetControl, browserLocationIndex, browserLocations, gestureAttempt, gestureIssuedFrame, gestureDeadline,
        browserReadiness = Game.InspectVisualScriptHistoryBrowserReadiness(), baseline = baselineDescription,
        baselineAppliedTransactions, baselineRedoTransactions,
        folderNavigation = folderNavigation.Evidence, folderPointer = Game.InspectStockFolderPointer(),
        current = Describe(Game.InspectVisualScriptHistoryNativeState()),
        coverage = "Actual asset-browser open, native add-property controls with automatic compiler generation, and fourteen toolbar Undo/Redo gestures. Initial native Save is fixture arrangement only; no Save or managed authoring commands during proof. One shared project load; no OS cursor/clipboard/focus automation.",
    };

    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                if (path.Length == 0)
                {
                    path = Game.CompleteVisualScriptHistoryFixturePreparation(); browserName = Path.GetFileNameWithoutExtension(path);
                    (nativeAssetControl, browserLocations) = Game.GetVisualScriptHistoryBrowserNavigation();
                }
                if (Click("tooltab/assets.folders")) SeekPhase(20); break;
            case 1: SelectAll(true); Next(); break;
            case 2:
                SelectAll(false);
                foreach (var character in browserName) io.AddInputCharacter(character);
                Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 5: if (Click(nativeAssetControl)) Next(); break;
            case 6: if (Click(nativeAssetControl)) Next(); break;
            case 7:
                var initial = Game.InspectVisualScriptHistoryNativeState();
                if (initial == null || !Game.DocumentGroupParityCaseQuiescent
                    || !Game.Controls.Centers.ContainsKey("visual-script/add-property")) break;
                Require(initial.Producer.Asset.Properties.Count == 0 && !initial.Producer.IsDirty && !initial.Generated.IsDirty,
                    "The native producer and compiler output must begin at their saved empty-property baseline.");
                Require(initial.Generated.Text == initial.CompilerText, "Baseline generated text must equal the actual native compiler output.");
                Require(initial.HistoryPosition >= 0 && initial.HistoryPosition <= initial.TransactionIds.Length,
                    "Native baseline applied position must describe a real prefix of the shared transaction stack");
                baseline = initial; baselineDescription = Describe(initial);
                baselineAppliedTransactions = initial.TransactionIds.Take(initial.HistoryPosition).ToArray();
                baselineRedoTransactions = initial.TransactionIds.Skip(initial.HistoryPosition).ToArray();
                baselineAppliedParticipants = initial.ParticipantGenerations.Take(initial.HistoryPosition)
                    .Select(generations => generations.ToArray()).ToArray();
                Game.CertifyVisualScriptHistoryFiles();
                checkpoints.Add(new { checkpoint = "native-saved-baseline", state = Describe(initial) });
                Next(); break;
            case 20:
                if (browserLocationIndex == browserLocations.Length)
                { if (Click("tooltab/assets.browser")) SeekPhase(25); break; }
                if (folderNavigation.Select(Game, browserLocations[browserLocationIndex], Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 21:
                if (!folderNavigation.Select(Game, browserLocations[browserLocationIndex], Click, Clock.Elapsed.TotalSeconds)) break;
                if (browserLocationIndex + 1 == browserLocations.Length
                    || Game.Controls.Expanded.GetValueOrDefault("location/" + browserLocations[browserLocationIndex]))
                { browserLocationIndex++; SeekPhase(20); break; }
                if (folderNavigation.Expand(Game, browserLocations[browserLocationIndex], Click, Clock.Elapsed.TotalSeconds))
                { browserLocationIndex++; SeekPhase(20); }
                break;
            case 22: io.AddKeyEvent(ImGuiKey.RightArrow, false); Next(); break;
            case 23:
                if (!Game.Controls.Expanded.GetValueOrDefault("location/" + browserLocations[browserLocationIndex])) break;
                browserLocationIndex++; SeekPhase(20); break;
            case 25:
                if (!Game.StockDispatchFiltersEmpty) { Click("asset/filter/clear"); break; }
                if (Click("asset/filter")) SeekPhase(1); break;
            case 8: if (BeginAddGesture(0)) Next(); break;
            case 9:
                var first = Game.InspectVisualScriptHistoryNativeState();
                if (first?.Producer.Asset.Properties.Count != 1)
                { RetryUnacceptedGesture(first, 0, 8); break; }
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                Require(Game.VisualScriptHistoryLastAcceptedFrame >= gestureIssuedFrame, "The first mutation must originate from the actual native add-property button's accepted gesture.");
                Certify(first, 1);
                firstOutput = first.Generated.Text;
                firstPropertyIds = first.Producer.Asset.Properties.Select(property => property.Id).ToArray();
                Require(firstOutput != baseline!.CompilerText && firstOutput.Contains("Member0", StringComparison.Ordinal),
                    "The first actual add-property must emit a meaningful native generated-source delta.");
                checkpoints.Add(new { checkpoint = "first-ui-generation", state = Describe(first) });
                gestureAttempt = 0; gesturePrepared = false;
                Next(); break;
            case 10: if (BeginAddGesture(1)) Next(); break;
            case 11:
                var second = Game.InspectVisualScriptHistoryNativeState();
                if (second?.Producer.Asset.Properties.Count != 2)
                { RetryUnacceptedGesture(second, 1, 10); break; }
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                Require(Game.VisualScriptHistoryLastAcceptedFrame >= gestureIssuedFrame, "The repeated mutation must originate from a second accepted native add-property gesture.");
                Certify(second, 2);
                secondOutput = second.Generated.Text;
                secondPropertyIds = second.Producer.Asset.Properties.Select(property => property.Id).ToArray();
                Require(secondOutput != firstOutput && secondOutput.Contains("Member1", StringComparison.Ordinal)
                    && secondPropertyIds[0] == firstPropertyIds[0],
                    "The repeated actual add-property must emit a distinct second native delta and preserve the first property identity.");
                ownedTransactions = second.TransactionIds.Skip(baselineAppliedTransactions.Length).ToArray();
                Require(ownedTransactions.Length == 2, "Two accepted UI producer edits must own exactly two native transactions.");
                checkpoints.Add(new { checkpoint = "second-ui-generation", state = Describe(second) });
                SeekPhase(100); break;
            case 100:
                if (historyStep == HistoryCommands.Length) { SeekPhase(200); break; }
                if (Click(HistoryCommands[historyStep].Control)) Next(); break;
            case 101:
                var current = Game.InspectVisualScriptHistoryNativeState();
                var expected = HistoryCommands[historyStep].Properties;
                if (current?.Producer.Asset.Properties.Count != expected || !Game.DocumentGroupParityCaseQuiescent) break;
                Certify(current, expected);
                var expectedText = expected switch { 0 => baseline!.CompilerText, 1 => firstOutput!, _ => secondOutput! };
                Require(current.Generated.Text == expectedText,
                    "Actual toolbar history must replay the exact previous generated-source delta, not repeat an obsolete closure.");
                var expectedIds = expected switch { 0 => Array.Empty<Guid>(), 1 => firstPropertyIds, _ => secondPropertyIds };
                Require(current.Producer.Asset.Properties.Select(property => property.Id).SequenceEqual(expectedIds),
                    "Actual toolbar history must retain the original native property identities.");
                Require(current.TransactionIds.Skip(baselineAppliedTransactions.Length).SequenceEqual(ownedTransactions),
                    "Repeated native history gestures must not append generated transactions or replace their identities.");
                checkpoints.Add(new { checkpoint = "ui-history", historyStep, control = HistoryCommands[historyStep].Control,
                    properties = expected, state = Describe(current) });
                historyStep++; SeekPhase(100); break;
            case 200:
                var restored = Game.InspectVisualScriptHistoryNativeState();
                if (restored == null || !Game.DocumentGroupParityCaseQuiescent) break;
                Certify(restored, 0);
                Require(!restored.Producer.IsDirty && !restored.Generated.IsDirty, "Final UI undo must restore both saved canonical baselines.");
                checkpoints.Add(new { checkpoint = "clean-native-baseline-restored", state = Describe(restored), savedFilesUnchanged = true });
                Game.CloseVisualScriptHistoryFixture();
                Finish(true, "Repeated native UI visual-script generation and fourteen toolbar Undo/Redo gestures preserved exact compiler text, identities, producer leases, transaction participants and saved files.");
                break;
        }
    }

    private void Certify(VisualScriptHistoryNativeState state, int expectedProperties)
    {
        Require(ReferenceEquals(state.ProducerEntry, baseline!.ProducerEntry) && ReferenceEquals(state.Producer, baseline.Producer)
            && ReferenceEquals(state.ProducerState, baseline.ProducerState) && ReferenceEquals(state.Generated, baseline.Generated)
            && ReferenceEquals(state.GeneratedState, baseline.GeneratedState),
            "All repeated UI generations/history gestures must retain the same canonical producer and generated source owners.");
        Require(state.Producer.Asset.Properties.Count == expectedProperties && state.Generated.Text == state.CompilerText
            && state.HistoryPosition == baseline.HistoryPosition + expectedProperties,
            "Native graph, exact compiler output and shared history position must agree after every actual UI gesture.");
        Require(state.TransactionIds.Take(baselineAppliedTransactions.Length).SequenceEqual(baselineAppliedTransactions),
            "The original applied shared project history prefix must remain unchanged.");
        for (var index = 0; index < baselineAppliedParticipants.Length; index++)
            Require(state.ParticipantGenerations[index].SequenceEqual(baselineAppliedParticipants[index]),
                "Each original applied shared transaction must retain its exact native owner participants");
        Require(!state.TransactionIds.Intersect(baselineRedoTransactions).Any(),
            "Actual new UI editing must truncate only the prior undone branch and must never replay its stale transactions");
        for (var index = baselineAppliedTransactions.Length; index < state.ParticipantGenerations.Length; index++)
            Require(state.ParticipantGenerations[index].Contains(state.ProducerState.Generation)
                && state.ParticipantGenerations[index].Contains(state.GeneratedState.Generation),
                "Each actual UI producer edit must retain both canonical producer and generated-source history participants.");
        Game.CertifyVisualScriptHistoryFiles();
    }

    private bool BeginAddGesture(int previousCount)
    {
        var current = Game.InspectVisualScriptHistoryNativeState();
        if (current == null || !Game.DocumentGroupParityCaseQuiescent) return false;
        Require(current.Producer.Asset.Properties.Count == previousCount, "The producer changed outside its expected accepted gesture.");
        if (!gesturePrepared)
        {
            gestureDeadline = Clock.Elapsed.TotalSeconds + 15;
            gestureBeforeText = current.Generated.Text; gestureBeforePosition = current.HistoryPosition;
            gestureBeforeTransactions = current.TransactionIds.ToArray();
            gestureBeforeProperties = current.Producer.Asset.Properties.Select(property => property.Id).ToArray();
            gesturePrepared = true;
        }
        Require(Clock.Elapsed.TotalSeconds < gestureDeadline && gestureAttempt < 3, "The actual native add-property target did not accept its bounded UI gesture.");
        if (!Game.Controls.Centers.TryGetValue("visual-script/add-property", out var point)) return false;
        if (!Game.VisualScriptHistoryAddPropertyTargetReady)
        { Aim("visual-script/add-property", point); return false; }
        if (Gui.IsAnyItemActive() || Gui.GetIO().MouseDown[0]) return false;
        if (!Click("visual-script/add-property")) return false;
        gestureIssuedFrame = Gui.GetFrameCount(); gestureAttempt++; return true;
    }

    private void RetryUnacceptedGesture(VisualScriptHistoryNativeState? current, int previousCount, int retryPhase)
    {
        Require(Clock.Elapsed.TotalSeconds < gestureDeadline, "The accepted native add-property gesture exceeded its shared deadline.");
        if (current == null || !Game.DocumentGroupParityCaseQuiescent || Gui.GetFrameCount() <= gestureIssuedFrame + 4
            || Gui.GetIO().MouseDown[0] || Gui.IsAnyItemActive()) return;
        Require(current.Producer.Asset.Properties.Count == previousCount && current.Generated.Text == gestureBeforeText
            && current.HistoryPosition == gestureBeforePosition && current.TransactionIds.SequenceEqual(gestureBeforeTransactions)
            && current.Producer.Asset.Properties.Select(property => property.Id).SequenceEqual(gestureBeforeProperties),
            "An incomplete UI gesture changed native graph, generated text or history unexpectedly.");
        Certify(current, previousCount);
        Require(Game.VisualScriptHistoryLastAcceptedFrame < gestureIssuedFrame,
            "The real native button accepted its gesture but the producer mutation did not execute.");
        Require(gestureAttempt < 3, "Three native add-property gestures were not accepted; preserve diagnostic evidence.");
        SeekPhase(retryPhase);
    }
    private static object? Describe(VisualScriptHistoryNativeState? state) => state == null ? null : new
    {
        entryId = state.ProducerEntry.Id, producerGeneration = state.ProducerState.Generation,
        generatedGeneration = state.GeneratedState.Generation, state.HistoryPosition, state.TransactionIds, state.ParticipantGenerations,
        propertyIds = state.Producer.Asset.Properties.Select(property => property.Id).ToArray(),
        propertyNames = state.Producer.Asset.Properties.Select(property => property.Name).ToArray(),
        generatedTextSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.Generated.Text))),
        compilerTextSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.CompilerText))),
        producerDirty = state.Producer.IsDirty, generatedDirty = state.Generated.IsDirty,
        generatedReadOnly = !state.Generated.CanEditText, actualProducerLease = state.Generated.IsGeneratedOutputOwnedBy(state.ProducerState),
    };
    private static void SelectAll(bool down)
    { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
    protected override void CleanInput()
    { SelectAll(false); Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Gui.GetIO().AddKeyEvent(ImGuiKey.RightArrow, false); }
}
