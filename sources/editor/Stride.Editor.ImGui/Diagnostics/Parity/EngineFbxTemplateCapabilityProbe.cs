using System.Text;
using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Assets.Materials;
using Stride.Assets.Textures;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineFbxTemplateCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private readonly string[] variants = ["model", "skeleton", "animation"];
    private readonly List<object> receipts = [];
    private int variantIndex, baselinePosition, importBaselinePosition, searchSteps, saveAttempts;
    private string baselineModelOptions = "";
    private Guid? optionsTransaction;
    private Guid importTransaction;
    private Dictionary<string, string> baselineAssetFiles = [];
    private string[] baselineIds = [];
    private string name = "", source = "";
    private AssetCreationPanel.Choice? choice;
    private FbxNativeOutput[] published = [];
    private bool chooserCancellation = true, optionsCancellation;
    private long saveBaseline, optionsAcceptedBaseline, optionsCanceledBaseline;
    private double saveAt;
    private string historyAction = "";
    private Guid historyTransaction;
    private int historyIndex, historyBefore, historyAfter, historyNext, historyAttempts, historyIssuedFrame;
    private double historyAt;
    private string? historyCommandPath;
    private readonly List<object> historyGestures = [];
    private string[] outputConfiguration = [];
    private int configurationIndex;
    internal object CapabilityEvidence => Evidence;
    protected override string Scenario => "asset-templates.fbx-import";
    protected override double PhaseTimeoutSeconds => 120;
    protected override object Evidence => new { variantIndex, baselinePosition, importBaselinePosition, optionsTransaction, history = Game.FbxTemplateHistory, toolbarHistory = Game.FbxHistoryToolbarEvidence, historyGestures, historyAction, historyAttempts, historyIssuedFrame, historyBefore, historyAfter, importTransaction, baselineAssetFiles, receipts, expectedName = name,
        draft = Game.CaptureAssetTemplateParityDraft(), importerDiagnostics = Game.CaptureFbxTemplateImportDiagnostics(), currentHistory = Game.AssetTemplateHistoryPosition, options = Game.FbxTemplateOptions, fixture = Game.FbxTemplateFixtureEvidence,
        publications = baselineIds.Length == 0 ? null : Game.CaptureAssetTemplatePublicationState(baselineIds),
        saveAttempts, saveBaseline, saveReceipt = new { Game.AssetTemplateSaveUiReceipt.Accepted, Game.AssetTemplateSaveUiReceipt.Eligible, Game.AssetTemplateSaveUiReceipt.Frame },
        sourceSelectionCoverage = "Retained file-selection seam; actual registered chooser/options/importer/publication/history/save. Platform file dialog input excluded." };
    protected override void Step()
    {
        switch (Phase)
        {
            case 0:
                if (!Game.AssetTemplateParityReady) return;
                if (variantIndex == variants.Length) { SeekPhase(60); return; }
                if (variantIndex == 0 && !Game.FbxTemplateOriginalWorkspaceClean) { BeginSaveGesture(); SeekPhase(70); return; }
                Game.VerifyFbxTemplateInputs(); baselineIds = Game.AssetTemplateNativeIds(); baselinePosition = Game.AssetTemplateHistoryPosition;
                baselineAssetFiles = Game.CaptureFbxBaselineAssetFiles();
                importBaselinePosition = baselinePosition; optionsTransaction = null; baselineModelOptions = Game.CaptureFbxModelOptions();
                name = "FbxAcceptance-" + variants[variantIndex] + "-" + Guid.NewGuid().ToString("N");
                source = Game.FbxTemplateSource(variants[variantIndex]); chooserCancellation = true; optionsCancellation = variantIndex == 0;
                SeekPhase(1); break;
            case 1:
                if (!Game.AssetTemplateParityReady) return;
                Game.OpenAssetTemplateParityChooser(); choice = Game.FbxTemplateChoice(variants[variantIndex]); searchSteps = 0; Next(); break;
            case 2: if (Click("template/search")) Next(); break;
            case 3: SelectAll(); Next(); break;
            case 4: Type(choice!.Name); Next(); break;
            case 5:
                Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false);
                if (Game.AssetTemplateSelected(choice!)) { Next(); return; }
                Require(++searchSteps < 30, "The actual chooser cannot reach its registered FBX template.");
                Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, true); SeekPhase(50); break;
            case 50: Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false); SeekPhase(5); break;
            case 6: if (Click("template/name")) Next(); break;
            case 7: SelectAll(); Next(); break;
            case 8: Type(name); Next(); break;
            case 9:
                if (!Game.AssetTemplateSelectionMatches(choice!, name)) return;
                if (chooserCancellation) { if (Click("template/cancel")) Next(); }
                else { Game.ArmAssetTemplateParitySource(choice!, source); SeekPhase(11); }
                break;
            case 10:
                if (Game.AssetTemplateChooserPending) return;
                AssertBaseline("Template Cancel"); chooserCancellation = false; SeekPhase(1); break;
            case 11: if (Click("template/create")) Next(); break;
            case 12:
                if (variants[variantIndex] != "model") { SeekPhase(30); return; }
                if (Game.FbxTemplateOptions is not { Pending: true } options) return;
                Require(Game.IsFbxTemplateImporterRegistered(options.ImporterId, source, choice!.AssetType), "Options must belong to the actual registered FBX importer.");
                Require(options.SupportsDeduplicate && options.SupportsSkeleton, "Stock model importer must expose its real deduplication/skeleton policy.");
                if (optionsCancellation)
                {
                    optionsCanceledBaseline = options.CanceledActions;
                    if (Click("fbx/options/cancel")) SeekPhase(13);
                }
                else
                {
                    outputConfiguration = options.Outputs.Keys.Where(type => type != typeof(ModelAsset).FullName && type != typeof(SkeletonAsset).FullName).ToArray();
                    configurationIndex = 0; SeekPhase(20);
                }
                break;
            case 13:
                if (Game.FbxTemplateOptionsPending || Game.AssetTemplatePublicationPending) return;
                Require(Game.FbxTemplateOptions!.CanceledActions == optionsCanceledBaseline + 1, "The native options Cancel widget did not acknowledge the action.");
                AssertBaseline("Import options Cancel"); optionsCancellation = false; SeekPhase(1); break;
            case 20:
                var configuring = Game.FbxTemplateOptions ?? throw new InvalidOperationException("The model options owner disappeared.");
                if (configurationIndex == outputConfiguration.Length) { SeekPhase(21); return; }
                var output = outputConfiguration[configurationIndex];
                var desired = output == typeof(MaterialAsset).FullName || output == typeof(TextureAsset).FullName;
                if (configuring.Outputs[output] != desired) { if (Click("fbx/options/output/" + output)) SeekPhase(51); }
                else configurationIndex++;
                break;
            case 51: SeekPhase(20); break;
            case 21:
                if (Game.FbxTemplateOptions!.Deduplicate) { if (Click("fbx/options/deduplicate")) SeekPhase(52); }
                else if (Click("fbx/options/skeleton/import")) SeekPhase(22);
                break;
            case 52: SeekPhase(21); break;
            case 22:
                var configured = Game.FbxTemplateOptions!;
                if (configured.ReuseSkeleton || !configured.Outputs.GetValueOrDefault(typeof(SkeletonAsset).FullName!)) return;
                Require(!configured.Deduplicate && configured.Outputs.GetValueOrDefault(typeof(ModelAsset).FullName!)
                    && configured.Outputs.GetValueOrDefault(typeof(MaterialAsset).FullName!) && configured.Outputs.GetValueOrDefault(typeof(TextureAsset).FullName!)
                    && !configured.Outputs.GetValueOrDefault(typeof(AnimationAsset).FullName!), "The actual model options must retain the requested outputs and skeleton/deduplication policy.");
                optionsAcceptedBaseline = configured.AcceptedActions;
                receipts.Add(new { variant = "model", selectedOptions = configured });
                if (Click("fbx/options/import")) SeekPhase(23); break;
            case 23:
                if (Game.FbxTemplateOptionsPending)
                {
                    if (Game.FbxTemplateOptions!.AcceptedActions == optionsAcceptedBaseline + 1 && !Game.AssetTemplatePublicationPending)
                        throw new InvalidOperationException("The actual Import action failed before starting its producer: " + Game.StatusMessage);
                    return;
                }
                Require(Game.FbxTemplateOptions!.AcceptedActions == optionsAcceptedBaseline + 1, "Native Import widget must accept exactly once.");
                importBaselinePosition = Game.FbxTemplateAcceptedOptionsHistory?.Position ?? throw new InvalidOperationException("Native model options acceptance has no history receipt.");
                if (importBaselinePosition == baselinePosition)
                    Require(Game.CaptureFbxModelOptions() == baselineModelOptions, "A no-op options acceptance changed native preferences without its history transaction.");
                else
                {
                    Game.CertifyFbxOptionsHistory(baselinePosition, importBaselinePosition);
                    optionsTransaction = Game.FbxTemplateAcceptedOptionsHistory!.Entries[baselinePosition].Id;
                }
                receipts.Add(new { variant = "model", optionsHistory = Game.FbxTemplateAcceptedOptionsHistory, optionsTransaction, baselineModelOptions });
                SeekPhase(30); break;
            case 30:
                if (Game.AssetTemplatePublicationPending || Game.AssetTemplateChooserPending || Game.FbxTemplateOptionsPending) return;
                var outputs = Game.InspectFbxTemplateOutputs(baselineIds, false);
                Require(outputs.Length > 0 && Game.AssetTemplateHistoryPosition == importBaselinePosition + 1, "The real importer must publish one whole batch transaction. Actual outputs=" + outputs.Length + ", history=" + Game.AssetTemplateHistoryPosition + ", baseline=" + baselinePosition + "; " + JsonSerializer.Serialize(Game.CaptureFbxTemplateImportDiagnostics()));
                Require(Game.FbxTemplateHistory.Entries[importBaselinePosition].Name == "Import assets", "The published output batch must own the actual native Import assets transaction.");
                importTransaction = Game.FbxTemplateHistory.Entries[importBaselinePosition].Id;
                receipts.Add(new { variant = variants[variantIndex], importTransaction = Game.FbxTemplateHistory.Entries[importBaselinePosition] });
                if (variants[variantIndex] == "model")
                    Require(outputs.Any(o => o.Type == typeof(ModelAsset).FullName && o.Materials > 0)
                        && outputs.Any(o => o.Type == typeof(SkeletonAsset).FullName && o.Nodes > 0)
                        && outputs.Any(o => o.Type == typeof(MaterialAsset).FullName) && outputs.Any(o => o.Type == typeof(TextureAsset).FullName)
                        && outputs.All(o => o.Type != typeof(AnimationAsset).FullName), "Model options did not publish their real selected source companions.");
                else Require(outputs.All(o => o.Type == choice!.AssetType.FullName), "Restricted skeleton/animation templates must retain their real root output policy.");
                Game.VerifyFbxTemplateInputs(); BeginSaveGesture(); SeekPhase(31); break;
            case 31:
                if (!Game.AssetTemplateSaveUiReceipt.Eligible) return;
                if (Click("toolbar/save")) { saveAttempts++; saveAt = Clock.Elapsed.TotalSeconds; Next(); }
                break;
            case 32:
                if (Game.AssetTemplateSaveUiReceipt.Accepted <= saveBaseline)
                {
                    if (Clock.Elapsed.TotalSeconds - saveAt < .35) return;
                    Require(saveAttempts < 3, "The actual Save widget did not accept three bounded synthetic attempts."); SeekPhase(31); return;
                }
                if (!Game.AssetTemplateSavedPublicationsReady(baselineIds)) return;
                Require(Game.AssetTemplateHistoryPosition == importBaselinePosition + 1, "Save must not create another importer transaction.");
                published = Game.InspectFbxTemplateOutputs(baselineIds, true); Game.VerifyFbxTemplateInputs();
                receipts.Add(new { variant = variants[variantIndex], template = choice!.Template!.Id, nativeOutputs = published });
                SeekPhase(33); break;
            case 33: BeginHistoryGesture("undo", importTransaction, importBaselinePosition, importBaselinePosition + 1, 34); break;
            case 34:
                if (Game.AssetTemplateHistoryPosition != importBaselinePosition) return;
                Require(Game.FbxTemplateHistory.Entries[importBaselinePosition].Id == importTransaction && !Game.FbxTemplateHistory.Entries[importBaselinePosition].IsDone,
                    "Undo moved a different native importer transaction.");
                AssertBaseline("Whole batch Undo", importBaselinePosition); Require(published.All(o => !File.Exists(o.Path)), "Undo left owned imported files behind.");
                BeginHistoryGesture("redo", importTransaction, importBaselinePosition, importBaselinePosition, 35); break;
            case 35:
                if (Game.AssetTemplateHistoryPosition != importBaselinePosition + 1) return;
                Require(Game.FbxTemplateHistory.Entries[importBaselinePosition].Id == importTransaction && Game.FbxTemplateHistory.Entries[importBaselinePosition].IsDone,
                    "Redo changed the native importer transaction identity.");
                var replayed = Game.InspectFbxTemplateOutputs(baselineIds, true);
                Require(JsonSerializer.Serialize(replayed) == JsonSerializer.Serialize(published), "Redo changed native imported IDs/types/files/source hashes or references.");
                Game.VerifyFbxTemplateInputs(); BeginHistoryGesture("undo", importTransaction, importBaselinePosition, importBaselinePosition + 1, 36); break;
            case 36:
                if (Game.AssetTemplateHistoryPosition != importBaselinePosition) return;
                AssertBaseline("Final owned batch Undo", importBaselinePosition); Require(published.All(o => !File.Exists(o.Path)), "Final importer cleanup retained its owned output files.");
                Game.VerifyFbxTemplateInputs();
                if (optionsTransaction is { } optionsId) BeginHistoryGesture("undo", optionsId, baselinePosition, importBaselinePosition, 37);
                else { variantIndex++; SeekPhase(0); }
                break;
            case 37:
                if (Game.AssetTemplateHistoryPosition != baselinePosition) return;
                AssertBaseline("Owned options Undo");
                Require(Game.CaptureFbxModelOptions() == baselineModelOptions, "Undo did not restore the original native model import preferences.");
                Require(Game.FbxTemplateHistory.Entries[baselinePosition].Id == optionsTransaction && !Game.FbxTemplateHistory.Entries[baselinePosition].IsDone,
                    "Options cleanup undid a different native history transaction.");
                variantIndex++; SeekPhase(0); break;
            case 80:
                Require(HistoryMatches(historyBefore), "The exact native transaction changed before the queued history gesture.");
                if (!Game.FbxHistoryToolbarEligible(historyAction)) return;
                if (Click("toolbar/" + historyAction))
                {
                    historyAttempts++; historyIssuedFrame = Gui.GetFrameCount(); historyAt = Clock.Elapsed.TotalSeconds;
                    historyCommandPath = Game.FbxHistoryCommandPath;
                    historyGestures.Add(new { action = historyAction, historyTransaction, historyBefore, historyAfter,
                        attempt = historyAttempts, queuedFrame = historyIssuedFrame, commandPath = historyCommandPath });
                    SeekPhase(81);
                }
                break;
            case 81:
                var accepted = Game.FbxHistoryToolbarAcceptance(historyAction);
                if (accepted.Frame >= historyIssuedFrame)
                {
                    Require(string.Equals(accepted.Path, historyCommandPath, StringComparison.OrdinalIgnoreCase),
                        "The accepted history action belonged to a different native command owner.");
                    // Accepted requests are never retried, including failed native replay.
                    Require(HistoryMatches(historyBefore) || HistoryMatches(historyAfter), "Accepted history replay changed a different native transaction or position.");
                    if (!HistoryMatches(historyAfter)) return;
                    historyGestures.Add(new { action = historyAction, historyTransaction, acceptedFrame = accepted.Frame,
                        accepted.Path, attempts = historyAttempts, completedPosition = Game.AssetTemplateHistoryPosition });
                    SeekPhase(historyNext); return;
                }
                Require(HistoryMatches(historyBefore), "History changed without the required native toolbar acceptance receipt.");
                if (Clock.Elapsed.TotalSeconds - historyAt < .35) return;
                Require(historyAttempts < 3, "The native history toolbar did not accept three bounded synthetic attempts.");
                SeekPhase(80); break;
            case 60:
                AssertBaseline("Restored original workspace");
                if (Game.FbxTemplateOriginalWorkspaceClean) { Finish(true, "Three registered FBX template variants passed native importer/options/source/history/persistence acceptance and restored the clean original workspace."); return; }
                BeginSaveGesture(); SeekPhase(70); break;
            case 70:
                if (!Game.AssetTemplateSaveUiReceipt.Eligible) return;
                if (Click("toolbar/save")) { saveAttempts++; saveAt = Clock.Elapsed.TotalSeconds; Next(); }
                break;
            case 71:
                if (Game.AssetTemplateSaveUiReceipt.Accepted <= saveBaseline)
                {
                    if (Clock.Elapsed.TotalSeconds - saveAt < .35) return;
                    Require(saveAttempts < 3, "Baseline Save UI did not accept its bounded queued input."); SeekPhase(70); return;
                }
                if (!Game.FbxTemplateOriginalWorkspaceClean) return;
                if (variantIndex == variants.Length) { AssertBaseline("Saved original workspace"); SeekPhase(60); }
                else SeekPhase(0);
                break;
        }
    }
    private void BeginHistoryGesture(string action, Guid transaction, int index, int before, int next)
    {
        historyAction = action; historyTransaction = transaction; historyIndex = index;
        historyBefore = before; historyAfter = before + (action == "undo" ? -1 : 1); historyNext = next;
        historyAttempts = 0; historyIssuedFrame = -1; SeekPhase(80);
    }
    private bool HistoryMatches(int position)
    {
        var history = Game.FbxTemplateHistory;
        return history.Position == position && historyIndex < history.Entries.Count
            && history.Entries[historyIndex].Id == historyTransaction
            && history.Entries[historyIndex].IsDone == (position > historyIndex);
    }
    private void BeginSaveGesture() { saveBaseline = Game.AssetTemplateSaveUiReceipt.Accepted; saveAttempts = 0; }
    private void AssertBaseline(string trigger, int? position = null)
    {
        Require(Game.AssetTemplateNativeIds().SequenceEqual(baselineIds)
            && Game.AssetTemplateHistoryPosition == (position ?? baselinePosition), trigger + " changed original native membership/history.");
        Game.VerifyFbxBaselineAssetFiles(baselineAssetFiles);
    }
    private static void SelectAll() { var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); }
    private static void Type(string value)
    {
        var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        foreach (var rune in value.EnumerateRunes()) io.AddInputCharacter((uint)rune.Value);
    }
    protected override void CleanInput() { Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false); Game.ClearAssetTemplateParitySource(); }
}
