using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

// Child actor: the grouped parent owns terminal drain and fixture teardown.
internal sealed class EngineAssetTemplateCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private readonly string[] branches = ["procedural", "heightmap", "spritesheet", "skybox"];
    private readonly List<object> receipts = [];
    private int branchIndex, creationIndex, baselinePosition, navigationFrames;
    private string[] baselineIds = [];
    private AssetCreationPanel.Choice? choice;
    private string name = "", input = "", inputHash = "";
    private AssetTemplateProof[] first = [], second = [];
    private bool cancelAttempt, canceledReference;
    private long saveAcceptedBaseline;
    private int saveAttempts;
    private double saveAttemptAt;
    private string expectedCollisionLeaf = "";
    private readonly Guid[] creationTransactions = new Guid[2];
    private readonly List<object> historyGestures = [];
    private string historyAction = "";
    private Guid historyTransaction;
    private int historyIndex, historyBefore, historyAfter, historyNext, historyAttempts, historyIssuedFrame;
    private double historyAt, historyStarted;
    private string? historyCommandPath;
    private string[] historyBeforeIds = [], historyBeforeFiles = [], historyPrefix = [];
    internal object CapabilityEvidence => Evidence;
    protected override string Scenario => "asset-template-acceptance";
    protected override object Evidence => new { branchIndex, creationIndex, baselinePosition, receipts, creationTransactions, historyGestures, historyAction, historyAttempts, historyIssuedFrame, toolbarHistory = Game.FbxHistoryToolbarEvidence, expectedName = name, saveAttempts, saveAcceptedBaseline, saveReceipt = new { Game.AssetTemplateSaveUiReceipt.Accepted, Game.AssetTemplateSaveUiReceipt.Eligible, Game.AssetTemplateSaveUiReceipt.Frame }, actualDraft = Game.CaptureAssetTemplateParityDraft(), publication = baselineIds.Length == 0 ? null : Game.CaptureAssetTemplatePublicationState(baselineIds),
        fileDialogCoverage = "Native source ingestion after a retained diagnostic file selection; platform file dialog input is excluded.",
        heightmapCoverage = "Original Heightmap template imports a source image; it is not the Skybox asset-reference branch." };
    protected override double PhaseTimeoutSeconds => 120;
    protected override void Step()
    {
        switch (Phase)
        {
            case 0:
                if (!Game.AssetTemplateParityReady) return;
                if (branchIndex == branches.Length) { Finish(true, "Four stock template branches passed native cancellation, collision, serialization and history acceptance."); return; }
                baselineIds = Game.AssetTemplateNativeIds(); baselinePosition = Game.AssetTemplateHistoryPosition;
                name = "TemplateAcceptance-" + branches[branchIndex] + "-" + Guid.NewGuid().ToString("N");
                input = branches[branchIndex] is "procedural" or "skybox" ? "" : Game.PrepareAssetTemplateParitySource(branches[branchIndex]);
                inputHash = branches[branchIndex] == "skybox" ? Game.AssetTemplateReferenceInputHash : input.Length == 0 ? "" : EditorGame.TemplateFileHash(input);
                historyPrefix = Game.FbxTemplateHistory.Entries.Take(baselinePosition).Select(e => e.Id.ToString()).ToArray();
                creationIndex = 0; cancelAttempt = true; canceledReference = false; SeekPhase(1); break;
            case 1:
                if (!Game.AssetTemplateParityReady) return;
                Game.OpenAssetTemplateParityChooser(); choice = Game.AssetTemplateParityChoice(branches[branchIndex]); navigationFrames = 0;
                Next(); break;
            case 2: if (Click("template/search")) Next(); break;
            case 3: SelectAll(); Next(); break;
            case 4: Type(choice!.Name); Next(); break;
            case 5:
                Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false);
                if (Game.AssetTemplateSelected(choice!)) { Next(); return; }
                Require(++navigationFrames < 30, "The actual filtered chooser cannot reach the stock factory registration.");
                Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, true); SeekPhase(25); break;
            case 25: Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false); SeekPhase(5); break;
            case 6: if (Click("template/name")) Next(); break;
            case 7: SelectAll(); Next(); break;
            case 8: Type(name); Next(); break;
            case 9:
                // ImGui may trickle queued Ctrl release and text across separate native frames.
                // Advance only after the genuine widget acknowledges the exact retained choice/name.
                if (!Game.AssetTemplateSelectionMatches(choice!, name)) return;
                if (cancelAttempt) { if (Click("template/cancel")) SeekPhase(10); }
                else if (branches[branchIndex] == "skybox") SeekPhase(30);
                else { if (input.Length != 0) Game.ArmAssetTemplateParitySource(choice!, input); SeekPhase(11); }
                break;
            case 10:
                if (Game.AssetTemplateChooserPending) return;
                Require(Game.AssetTemplateNativeIds().SequenceEqual(baselineIds) && Game.AssetTemplateHistoryPosition == baselinePosition,
                    "Cancel changed native membership or authored history.");
                receipts.Add(new { branch = branches[branchIndex], canceled = true, history = baselinePosition });
                cancelAttempt = false; SeekPhase(1); break;
            case 11: if (Click("template/create")) Next(); break;
            case 12:
                if (Game.AssetTemplatePublicationPending || Game.AssetTemplateChooserPending) return;
                Require(Game.AssetTemplateNativePublicationCount(baselineIds) == creationIndex + 1
                    && Game.AssetTemplateHistoryPosition == baselinePosition + creationIndex + 1,
                    "The real Create action must publish one canonical native asset and one transaction before Save.");
                saveAcceptedBaseline = Game.AssetTemplateSaveUiReceipt.Accepted; saveAttempts = 0; SeekPhase(40); break;
            case 40:
                if (!Game.AssetTemplateSaveUiReceipt.Eligible) return;
                if (Click("toolbar/save")) { saveAttempts++; saveAttemptAt = Clock.Elapsed.TotalSeconds; Next(); }
                break;
            case 41:
                if (Game.AssetTemplateSaveUiReceipt.Accepted <= saveAcceptedBaseline)
                {
                    if (Clock.Elapsed.TotalSeconds - saveAttemptAt < .35) return;
                    Require(saveAttempts < 3, "Three synthetic Save attempts were not accepted by the actual toolbar widget; inspect eligibility/frame receipts.");
                    SeekPhase(40); return;
                }
                if (!Game.AssetTemplateSavedPublicationsReady(baselineIds)) return;
                SeekPhase(42); break;
            case 42:
                var published = Game.InspectAssetTemplatePublications(baselineIds, input.Length == 0 ? null : input);
                Require(published.Length == creationIndex + 1 && Game.AssetTemplateHistoryPosition == baselinePosition + creationIndex + 1,
                    "Each real template publication must add exactly one native asset and one transaction.");
                var createdHistory = Game.FbxTemplateHistory;
                Require(createdHistory.Entries[baselinePosition + creationIndex].IsDone,
                    "The native publication transaction is not applied.");
                creationTransactions[creationIndex] = createdHistory.Entries[baselinePosition + creationIndex].Id;
                if (creationIndex == 1) Require(creationTransactions[0] == createdHistory.Entries[baselinePosition].Id
                    && creationTransactions[0] != creationTransactions[1], "Publication replaced the first native transaction.");
                if (branches[branchIndex] == "skybox") Require(published.All(p => p.ReferenceId == Game.AssetTemplateReferenceId && p.InputHash == inputHash), "Skybox serialization lost the chosen actual native texture reference.");
                Require(published.All(p => input.Length == 0 || p.InputHash == inputHash && p.SourceHash == inputHash), "Source ingestion changed the PNG bytes or lost its native source.");
                if (branches[branchIndex] == "spritesheet") Require(published.All(p => p.SpriteCount == 1 && p.Width == 16 && p.Height == 16), "SpriteSheet import lost its actual source region.");
                if (creationIndex == 0)
                {
                    first = published;
                    var firstLeaf = Path.GetFileNameWithoutExtension(first.Single().Path);
                    expectedCollisionLeaf = Stride.Core.NamingHelper.ComputeNewName(firstLeaf,
                        candidate => string.Equals(candidate.ToString(), firstLeaf, StringComparison.OrdinalIgnoreCase), "{0}_{1}");
                    creationIndex++; SeekPhase(1);
                }
                else
                {
                    second = published;
                    Require(second.Select(p => p.Id).Distinct().Count() == 2 && second.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2
                        && second.Any(p => Path.GetFileNameWithoutExtension(p.Path) == expectedCollisionLeaf)
                        && second.Any(p => p.Id == first.Single().Id && p.Path == first.Single().Path && p.DiskHash == first.Single().DiskHash), "Collision publication must preserve both native owners and choose a distinct name.");
                    receipts.Add(new { branch = branches[branchIndex], template = choice!.Template?.Id, factory = choice.Factory?.GetType().FullName, native = second });
                    SeekPhase(13);
                }
                break;
            case 30: if (Click("template/reference/open")) Next(); break;
            case 31:
                if (!canceledReference) { if (Click("template/reference/cancel")) SeekPhase(32); }
                else if (Click("template/reference/asset/" + Game.AssetTemplateReferenceId)) SeekPhase(33);
                break;
            case 32:
                if (!Game.Controls.Centers.ContainsKey("template/reference/open")) return;
                Require(Game.AssetTemplateReferenceEmpty && Game.AssetTemplateNativeIds().SequenceEqual(creationIndex == 0 ? baselineIds : baselineIds.Concat(first.Select(p => p.Id)).Order().ToArray())
                    && Game.AssetTemplateHistoryPosition == baselinePosition + creationIndex, "Canceling the reference chooser mutated native ownership/history.");
                canceledReference = true; SeekPhase(30); break;
            case 33: if (Click("template/reference/accept")) Next(); break;
            case 34:
                if (!Game.Controls.Centers.ContainsKey("template/create")) return;
                Require(Game.AssetTemplateReferenceChosen && Game.AssetTemplateSelectionMatches(choice!, name), "Actual texture picker acceptance lost the retained Skybox owner/name.");
                SeekPhase(11); break;
            case 13: BeginHistoryGesture("undo", 1, baselinePosition + 2, 14); break;
            case 14:
                Require(Game.AssetTemplateHistoryPosition == baselinePosition + 1 && Game.InspectAssetTemplatePublications(baselineIds, null).Length == 1,
                    "Undo must remove the second publication only.");
                BeginHistoryGesture("undo", 0, baselinePosition + 1, 15); break;
            case 15:
                Require(Game.AssetTemplateHistoryPosition == baselinePosition && Game.AssetTemplateNativeIds().SequenceEqual(baselineIds), "Undo must restore exact native membership.");
                Require(second.All(p => !File.Exists(p.Path)), "Undo retained owned published files.");
                BeginHistoryGesture("redo", 0, baselinePosition, 16); break;
            case 16: BeginHistoryGesture("redo", 1, baselinePosition + 1, 17); break;
            case 17:
                Require(Game.AssetTemplateHistoryPosition == baselinePosition + 2, "Redo did not replay both native publications.");
                var replayed = Game.InspectAssetTemplatePublications(baselineIds, input.Length == 0 ? null : input);
                Require(replayed.OrderBy(p => p.Id).SequenceEqual(second.OrderBy(p => p.Id)), "Redo changed native identity, serialized output or source hashes.");
                BeginHistoryGesture("undo", 1, baselinePosition + 2, 18); break;
            case 18: BeginHistoryGesture("undo", 0, baselinePosition + 1, 19); break;
            case 80:
                Require(Clock.Elapsed.TotalSeconds - historyStarted < PhaseTimeoutSeconds, "Native history gesture exceeded its bounded acknowledgement deadline.");
                Require(HistoryMatches(historyBefore) && HistoryBeforePublicationMatches(), "The exact native history/publication owner changed before the queued gesture.");
                if (!Game.AssetTemplateParityReady || !Game.FbxHistoryToolbarEligible(historyAction)) return;
                if (Click("toolbar/" + historyAction))
                {
                    historyAttempts++; historyIssuedFrame = Gui.GetFrameCount(); historyAt = Clock.Elapsed.TotalSeconds;
                    historyCommandPath = Game.FbxHistoryCommandPath;
                    historyGestures.Add(new { branch = branches[branchIndex], action = historyAction, historyTransaction,
                        historyBefore, historyAfter, attempt = historyAttempts, queuedFrame = historyIssuedFrame, historyCommandPath });
                    SeekPhase(81);
                }
                break;
            case 81:
                Require(Clock.Elapsed.TotalSeconds - historyStarted < PhaseTimeoutSeconds, "Accepted native history replay did not complete within its bounded deadline.");
                var accepted = Game.FbxHistoryToolbarAcceptance(historyAction);
                if (accepted.Frame >= historyIssuedFrame)
                {
                    Require(string.Equals(accepted.Path, historyCommandPath, StringComparison.OrdinalIgnoreCase), "The native toolbar accepted a different command owner.");
                    Require(HistoryMatches(historyBefore) || HistoryMatches(historyAfter), "Accepted history replay changed a different transaction or position.");
                    // An acknowledged request is never retried, including a failed native replay.
                    if (!HistoryMatches(historyAfter)) return;
                    historyGestures.Add(new { branch = branches[branchIndex], action = historyAction, historyTransaction,
                        acceptedFrame = accepted.Frame, accepted.Path, attempts = historyAttempts, completedPosition = Game.AssetTemplateHistoryPosition });
                    SeekPhase(historyNext); return;
                }
                Require(HistoryMatches(historyBefore) && HistoryBeforePublicationMatches(), "Native history/publication changed without a toolbar acceptance receipt.");
                if (Clock.Elapsed.TotalSeconds - historyAt < .35) return;
                Require(historyAttempts < 3, "Three bounded native toolbar gestures were not accepted; inspect native receipt evidence.");
                SeekPhase(80); break;
            case 19:
                Require(Game.AssetTemplateNativeIds().SequenceEqual(baselineIds) && Game.AssetTemplateHistoryPosition == baselinePosition, "Owned acceptance cleanup failed native membership restoration.");
                if (branches[branchIndex] == "skybox") Require(Game.AssetTemplateReferenceInputHash == inputHash, "Reference picker/history mutated the original texture PNG.");
                if (input.Length != 0) Require(EditorGame.TemplateFileHash(input) == inputHash, "History replay mutated input PNG bytes.");
                branchIndex++; SeekPhase(0); break;
        }
    }
    private void BeginHistoryGesture(string action, int creation, int before, int next)
    {
        historyAction = action; historyTransaction = creationTransactions[creation]; historyIndex = baselinePosition + creation;
        historyBefore = before; historyAfter = before + (action == "undo" ? -1 : 1); historyNext = next;
        historyAttempts = 0; historyIssuedFrame = -1; historyStarted = Clock.Elapsed.TotalSeconds;
        Require(HistoryMatches(before), "The next native Undo/Redo selector is not the exact creation transaction.");
        historyBeforeIds = Game.AssetTemplateNativeIds(); historyBeforeFiles = CaptureHistoryFiles(); SeekPhase(80);
    }
    private bool HistoryMatches(int position)
    {
        var history = Game.FbxTemplateHistory;
        return history.Position == position && historyIndex < history.Entries.Count
            && baselinePosition + 1 < history.Entries.Count
            && history.Entries[baselinePosition].Id == creationTransactions[0]
            && history.Entries[baselinePosition + 1].Id == creationTransactions[1]
            && history.Entries.Skip(baselinePosition).Take(2).Select((entry, index) => entry.IsDone == (position > baselinePosition + index)).All(done => done)
            && history.Entries[historyIndex].Id == historyTransaction
            && history.Entries[historyIndex].IsDone == (position > historyIndex)
            && history.Entries.Take(baselinePosition).All(e => e.IsDone)
            && history.Entries.Take(baselinePosition).Select(e => e.Id.ToString()).SequenceEqual(historyPrefix)
            && (position != historyBefore || (historyAction == "undo" ? position - 1 : position) == historyIndex);
    }
    private string[] CaptureHistoryFiles() => second.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
        .Select(p => p.Path + "|" + (File.Exists(p.Path) ? EditorGame.TemplateFileHash(p.Path) : "absent")).ToArray();
    private bool HistoryBeforePublicationMatches() => Game.AssetTemplateNativeIds().SequenceEqual(historyBeforeIds)
        && CaptureHistoryFiles().SequenceEqual(historyBeforeFiles);
    private static void SelectAll() { var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); }
    private static void Type(string value)
    {
        var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        foreach (var character in value.EnumerateRunes()) io.AddInputCharacter((uint)character.Value);
    }
    protected override void CleanInput()
    {
        Gui.GetIO().AddKeyEvent(ImGuiKey.DownArrow, false);
        Game.ClearAssetTemplateParitySource();
        // Never delete source files or owners while a native producer can still retain them.
        // The grouped parent drains and disposes the private project at terminal completion.
    }
}
