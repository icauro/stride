using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineStockVisualScriptSaveCapabilityProbe(EditorGame game, string artifacts)
    : EngineUiCapabilityProbe(game, artifacts)
{
    private string control = "", path = "";
    private string[] locations = [];
    private int locationIndex;
    private readonly StockBrowserDispatchAttempt browserOpen = new();
    private bool reopenStarted;
    private readonly StockFolderNavigationAttempt folderNavigation = new();
    private int baselinePropertyCount;
    private int closedSinceFrame = -1;
    private VisualScriptHistoryNativeState? baseline, edited, saved, reopened;
    private object? savedDisk, reopenedDisk;
    protected override string Scenario => "stock-dispatch.visualscript";
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new { path, control, locations, locationIndex, savedDisk, reopenedDisk,
        close = path.Length == 0 ? null : Game.InspectStockVisualScriptClose(path), browserAttempt = browserOpen.Evidence,
        coverage = "Actual browser VisualScript dispatch, native add-property/compiler update, toolbar Save, independent native producer/generated-file reads and actual clean tab close/browser reopen. No direct authored service mutation or physical input." };
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                if (control.Length == 0) { (control, locations) = Game.GetVisualScriptHistoryBrowserNavigation(); path = Game.StockVisualScriptBrowserPath; }
                if (Click("tooltab/assets.folders")) SeekPhase(20); break;
            case 20:
                if (locationIndex == locations.Length) { if (Click("tooltab/assets.browser")) SeekPhase(25); break; }
                if (folderNavigation.Select(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 21:
                if (!folderNavigation.Select(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds)) break;
                if (locationIndex + 1 == locations.Length || Game.Controls.Expanded.GetValueOrDefault("location/" + locations[locationIndex]))
                { locationIndex++; SeekPhase(20); break; }
                if (folderNavigation.Expand(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds))
                { locationIndex++; SeekPhase(20); }
                break;
            case 22: io.AddKeyEvent(ImGuiKey.RightArrow, false); SeekPhase(20); break;
            case 25:
                if (!Game.StockDispatchFiltersEmpty) { Click("asset/filter/clear"); break; }
                if (Click("asset/filter")) SeekPhase(1); break;
            case 1: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 2:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "VisualScriptHistory") io.AddInputCharacter(c); Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 5: browserOpen.Begin(Clock.Elapsed.TotalSeconds); if (Click(control)) Next(); break;
            case 6: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 7:
                baseline = Game.InspectVisualScriptHistoryNativeState();
                if (baseline == null || !Game.VerifyRecoveryAcceptanceNativeTab(baseline.Producer.Path, out _)) break;
                path = baseline.Producer.Path;
                Require(!baseline.Producer.IsDirty && !baseline.Generated.IsDirty, "VisualScript Save tail must begin from the real clean saved native producer/compiler baseline");
                baselinePropertyCount = baseline.Producer.Asset.Properties.Count;
                if (Click("visual-script/add-property")) Next(); break;
            case 8:
                edited = Game.InspectVisualScriptHistoryNativeState();
                if (edited == null || edited.Producer.Asset.Properties.Count != baselinePropertyCount + 1) break;
                Require(edited.Producer.IsDirty && edited.Generated.IsDirty && edited.Generated.Text == edited.CompilerText
                    && ReferenceEquals(edited.ProducerState, baseline!.ProducerState) && edited.HistoryPosition == baseline.HistoryPosition + 1,
                    "Actual native property gesture must update the current producer and real compiler-owned generated source");
                if (Click("toolbar/save")) Next(); break;
            case 9:
                saved = Game.InspectVisualScriptHistoryNativeState();
                if (saved == null || saved.Producer.IsDirty || saved.Generated.IsDirty) break;
                savedDisk = Game.VerifyStockVisualScriptSavedFiles(saved);
                if (!Game.Controls.Centers.TryGetValue("document-tab/" + saved.ProducerEntry.Id, out var tab)
                    || !Aim("document-tab/" + saved.ProducerEntry.Id, tab)) break;
                io.AddMouseButtonEvent(1, true); Next(); break;
            case 10: io.AddMouseButtonEvent(1, false); Next(); break;
            case 11: if (Click("document-close/" + saved!.ProducerEntry.Id + "/close")) Next(); break;
            case 12:
                if (!Game.StockVisualScriptProducerClosed(path)) { closedSinceFrame = -1; break; }
                if (closedSinceFrame < 0) closedSinceFrame = Gui.GetFrameCount();
                if (Gui.GetFrameCount() < closedSinceFrame + 3) break;
                Game.VerifyStockVisualScriptClosedProducerRetained(saved!);
                if (!reopenStarted) { browserOpen.Reset(); reopenStarted = true; }
                browserOpen.Begin(Clock.Elapsed.TotalSeconds);
                if (Click(control)) Next(); break;
            case 13: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 14:
                reopened = Game.InspectVisualScriptHistoryNativeState();
                if (reopened == null || !Game.VerifyRecoveryAcceptanceNativeTab(path, out var pane)) break;
                Require(!reopened.Producer.IsDirty && !reopened.Generated.IsDirty
                    && reopened.Producer.Asset.Id == saved!.Producer.Asset.Id
                    && reopened.CompilerText == saved.CompilerText
                    && reopened.Producer.Asset.Properties.Select(property => property.Id).SequenceEqual(saved.Producer.Asset.Properties.Select(property => property.Id)),
                    "Actual VisualScript close/default browser reopen must retain saved native producer/property identities and current compiler projection");
                Require(ReferenceEquals(reopened.Producer, saved.Producer)
                    && ReferenceEquals(reopened.ProducerState, saved.ProducerState)
                    && ReferenceEquals(reopened.Generated, saved.Generated)
                    && ReferenceEquals(reopened.GeneratedState, saved.GeneratedState)
                    && reopened.HistoryPosition == saved.HistoryPosition
                    && reopened.TransactionIds.SequenceEqual(saved.TransactionIds)
                    && reopened.ParticipantGenerations.Length == saved.ParticipantGenerations.Length
                    && reopened.ParticipantGenerations.Zip(saved.ParticipantGenerations)
                        .All(pair => pair.First.SequenceEqual(pair.Second)),
                    "Clean browser reopen must preserve exact canonical producer/generated owners and authored history participants without republishing a generation");
                reopenedDisk = new { files = Game.VerifyStockVisualScriptSavedFiles(reopened), pane,
                    producerGeneration = reopened.ProducerState.Generation, generatedGeneration = reopened.GeneratedState.Generation,
                    retainedSameProducer = ReferenceEquals(reopened.Producer, saved.Producer) };
                Finish(true, "Actual native VisualScript property/Save/independent producer-output reload/clean close/browser reopen passed");
                break;
        }
    }
}

internal sealed partial class EditorGame
{
    internal object InspectStockVisualScriptClose(string path)
    {
        var entry = documents.Find(path);
        var state = authoring?.Find(path);
        var nativeSelected = VerifyRecoveryAcceptanceNativeTab(path, out var nativeTab);
        return new
        {
            path, entryId = entry?.Id, entryDocument = entry?.Document.GetType().FullName,
            entries = documents.Entries.Select(item => new { item.Id, item.Path, generation = item.State?.Generation }).ToArray(),
            canonicalGeneration = state?.Generation, canonicalPublished = state?.IsPublished, canonicalDisposed = state?.IsDisposed,
            canonicalDirty = state?.Document.IsDirty, canonicalType = state?.Document.GetType().FullName,
            pendingCloseId = pendingDocumentClose?.Id, closeOwnerId = closingDocument?.Id,
            openDocumentClose, documentCloseError, CanPumpDocumentCloseRequests,
            nativeSelected, nativeTab, browserDispatch = InspectStockBrowserDispatch(path)
        };
    }
    internal string StockVisualScriptBrowserPath => visualScriptHistoryFixturePath ?? throw new InvalidOperationException("Native stock VisualScript fixture path is unavailable.");
    internal bool StockVisualScriptProducerClosed(string path) => documents.Find(path) == null;
    internal void VerifyStockVisualScriptClosedProducerRetained(VisualScriptHistoryNativeState saved)
    {
        if (documents.Find(saved.Producer.Path) != null || !ReferenceEquals(authoring?.Find(saved.Producer.Path), saved.ProducerState)
            || !saved.ProducerState.IsPublished || saved.ProducerState.IsDisposed
            || !saved.Generated.IsGeneratedOutputOwnedBy(saved.ProducerState)
            || saved.Generated.Text != saved.CompilerText || saved.Producer.IsDirty || saved.Generated.IsDirty)
            throw new InvalidOperationException("Clean producer view closure must preserve the current canonical native producer/compiler lease without republishing a tab.");
    }
    internal object VerifyStockVisualScriptSavedFiles(VisualScriptHistoryNativeState state)
    {
        var disk = Stride.Core.Assets.AssetFileSerializer.Load<Stride.Assets.Scripts.VisualScriptAsset>(state.Producer.Path).Asset;
        if (disk.Id != state.Producer.Asset.Id
            || !disk.Properties.Select(property => property.Id).SequenceEqual(state.Producer.Asset.Properties.Select(property => property.Id))
            || JsonSerializer.Serialize(disk.Properties.Select(property => property.Name)) != JsonSerializer.Serialize(state.Producer.Asset.Properties.Select(property => property.Name))
            || File.ReadAllText(state.Generated.Path) != state.CompilerText || state.Generated.Text != state.CompilerText
            || !state.Generated.IsGeneratedOutputOwnedBy(state.ProducerState) || state.Generated.CanEditText
            || documents.Entries.Count(entry => SamePath(entry.Path, state.Producer.Path)) != 1)
            throw new InvalidOperationException("Independent native saved VisualScript/generated reads lost producer/property/compiler/lease identity.");
        return new { nativeProducerId = disk.Id.ToString(), propertyIds = disk.Properties.Select(property => property.Id).ToArray(),
            names = disk.Properties.Select(property => property.Name).ToArray(), actualCompilerText = state.CompilerText,
            generatedPath = state.Generated.Path, actualNativeLease = true };
    }
}
