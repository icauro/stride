using System.Security.Cryptography;
using System.Text.Json;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineRecoveryWorkspaceCapabilityProbe
{
    private EditorGame.CorruptRecoveryBaseline? corruptRecoveryBaseline;
    private int corruptClickFrame;
    private string? corruptStableSignature;
    private int corruptStableFrames;
    private readonly bool corruptDirtyOwner = Environment.GetCommandLineArgs().Contains("--recovery-corrupt-dirty-owner");
    private bool corruptDirtyVerified;
    private object? corruptRefusalComparison;
    internal object? CorruptPayloadRecovery { get; private set; }

    private bool StepCorruptPayloadScenario()
    {
        if (producer || !Environment.GetCommandLineArgs().Contains("--recovery-corrupt-payload")) return false;
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || Game.VerificationDocumentViewport(Game.Document)?.RuntimeScene == null) break;
                Require(receipt != null && (corruptDirtyOwner && dirtyOwnerPrepared || !Game.Document.IsDirty), "Corrupt receiver must initially load actual original clean native data.");
                AssertOriginalDisks();
                var certificatePath = Path.Combine(Path.GetDirectoryName(receiptPath)!, "corrupt-payload.json");
                using (var certificate = JsonDocument.Parse(File.ReadAllBytes(certificatePath)))
                {
                    var value = certificate.RootElement;
                    Require(value.GetProperty("SessionId").GetGuid() == receipt!.SessionId
                        && value.GetProperty("PayloadHash").GetString() == receipt.DirtyPayloadHash
                        && value.GetProperty("ManifestSha256").GetString() == receipt.ManifestSha256,
                        "Corruption must belong to the real durable producer receipt.");
                    var payload = Path.Combine(Path.GetDirectoryName(receipt.ManifestPath)!, receipt.DirtyPayloadHash + ".snapshot");
                    Require(Same(value.GetProperty("PayloadPath").GetString()!, payload)
                        && Hash(payload) == value.GetProperty("CorruptSha256").GetString()
                        && Hash(payload) != receipt.DirtyPayloadHash && Hash(receipt.ManifestPath) == receipt.ManifestSha256,
                        "Actual snapshot is corrupt while original immutable manifest remains unchanged.");
                }
                if (corruptDirtyOwner && !dirtyOwnerPrepared) { SeekPhase(90); break; }
                Next(); break;
            case 3:
                var target = "recovery/document/" + receipt!.SessionId + "/" + receipt.DirtyPayloadHash;
                if (!Game.Controls.Centers.ContainsKey(target)) { ClickRecovery(target, 1); break; }
                if (!Game.IsCorruptRecoveryReceiverSettled) { corruptStableFrames = 0; break; }
                var current = Game.CaptureCorruptRecoveryBaseline(receipt.OriginalDisks.Select(item => item.Path)
                    .Concat(receipt.Sources.Select(item => item.Membership.Descriptor.Path)));
                var signature = Game.CorruptRecoveryStabilitySignature(current);
                if (signature != corruptStableSignature) { corruptStableSignature = signature; corruptStableFrames = 0; }
                if (++corruptStableFrames < 8) break;
                corruptRecoveryBaseline = current;
                if (ClickRecovery(target, 1))
                { corruptClickFrame = Hexa.NET.ImGui.ImGui.GetFrameCount(); Next(); }
                break;
            case 4:
                var action = Game.InspectCorruptRecoveryAction();
                if (action == null || action.Frame < corruptClickFrame) break;
                Require(action.Session == receipt!.SessionId && action.PayloadHash == receipt.DirtyPayloadHash
                    && Same(action.Path, receipt.DirtyPath) && !action.HostRestoreInvoked
                    && action.ErrorType == typeof(InvalidDataException).FullName
                    && action.Error?.Contains("integrity check", StringComparison.OrdinalIgnoreCase) == true,
                    "Real recovery Restore activation must reject corrupt native payload before calling canonical publication.");
                var comparison = Game.InspectCorruptRecoveryBaseline(corruptRecoveryBaseline!);
                CorruptPayloadRecovery = new { actualDocumentRestoreActivation = action, comparison,
                    noCanonicalPublication = !action.HostRestoreInvoked, originalOwnersHistoryNativeViewsAndDiskPreserved = false };
                Game.VerifyCorruptRecoveryBaseline(corruptRecoveryBaseline!);
                AssertOriginalDisks();
                if (corruptDirtyOwner)
                {
                    Require(retainedDirtyScene!.IsDirty && RetainedCompensationDataEquals(EditedX)
                        && Game.InspectDocumentGroupParity(retainedDirtyEntry)?.Generation == retainedDirtyGeneration
                        && retainedDirtyScene.History.Position == retainedCleanHistory!.Position + 1
                        && retainedDirtyScene.History.Entries[retainedDirtyScene.History.Position - 1].Id == ownedDirtyTransaction,
                        "Corrupt Restore refusal must preserve actual original dirty authored/runtime data and its exact nonzero native transaction.");
                    corruptDirtyVerified = true;
                }
                CorruptPayloadRecovery = new { actualDocumentRestoreActivation = action, comparison,
                    noCanonicalPublication = true, originalOwnersHistoryNativeViewsAndDiskPreserved = true,
                    genuineProducerSession = receipt.SessionId, manifestSha256 = receipt.ManifestSha256 };
                if (Click("recovery/close")) Next();
                break;
            case 5:
                var finalAction = Game.InspectCorruptRecoveryAction();
                var finalComparison = Game.InspectCorruptRecoveryBaseline(corruptRecoveryBaseline!);
                CorruptPayloadRecovery = new { actualDocumentRestoreActivation = finalAction, comparison = finalComparison,
                    noCanonicalPublication = finalAction?.HostRestoreInvoked == false, originalOwnersHistoryNativeViewsAndDiskPreserved = false };
                Game.VerifyCorruptRecoveryBaseline(corruptRecoveryBaseline!);
                AssertOriginalDisks();
                corruptRefusalComparison = finalComparison;
                CorruptPayloadRecovery = new { actualDocumentRestoreActivation = finalAction, comparison = finalComparison,
                    noCanonicalPublication = true, originalOwnersHistoryNativeViewsAndDiskPreserved = true,
                    dirtyRetainedOwner = corruptDirtyOwner, dirtyOwnerPreserved = corruptDirtyVerified,
                    genuineProducerSession = receipt!.SessionId, manifestSha256 = receipt.ManifestSha256 };
                if (corruptDirtyOwner) { SeekPhase(110); break; }
                Finish(true, "E08.recovery.corrupt PASS: actual owned crash producer, corrupted certified snapshot and real receiver Restore refusal before canonical publication; original owner/history/native views/disk retained, outer terminal required.");
                break;
            case 90: if (ClickRecovery("recovery/close", -1)) Next(); break;
            case 91:
                retainedDirtyScene ??= Game.Document ?? throw new InvalidOperationException("The real retained native scene is absent.");
                retainedDirtyEntry = Game.VerificationDocumentId(retainedDirtyScene)!.Value;
                retainedDirtyGeneration = Game.InspectDocumentGroupParity(retainedDirtyEntry)?.Generation ?? throw new InvalidOperationException("The retained native generation is absent.");
                if (Click("entity/Imported cube")) Next(); break;
            case 92:
                if (retainedDirtyScene!.Selected?.Name != "Imported cube") break;
                retainedDirtyEntity = retainedDirtyScene.Selected.Id;
                retainedOriginalX = retainedDirtyScene.Selected.Transform.Position.X;
                retainedCleanHistory = retainedDirtyScene.History;
                Require(!retainedDirtyScene.IsDirty && retainedOriginalX != EditedX, "Dirty corruption fixture must begin with original clean native data.");
                if (ClickInspector("Position/0")) Next(); break;
            case 93: SelectAll(true); Next(); break;
            case 94: SelectAll(false); Type(EditedX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); Next(); break;
            case 95: Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 96: Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 97: if (Click("entity/Imported cube")) Next(); break;
            case 98:
                if (!RetainedCompensationDataEquals(EditedX)) break;
                var edited = retainedDirtyScene!.History;
                Require(retainedDirtyScene.IsDirty && edited.Position == retainedCleanHistory!.Position + 1
                    && edited.Entries.Count == retainedCleanHistory.Entries.Count + 1
                    && edited.Entries.Take(retainedCleanHistory.Entries.Count).SequenceEqual(retainedCleanHistory.Entries),
                    "Actual Position widget must create exactly one native dirty transaction.");
                ownedDirtyTransaction = edited.Entries[edited.Position - 1].Id;
                Require(ownedDirtyTransaction != Guid.Empty && edited.Entries[edited.Position - 1].IsDone,
                    "The retained receiver must own a genuinely applied transaction.");
                AssertOriginalDisks(); dirtyOwnerPrepared = true; Next(); break;
            case 99: if (Click("menu/file")) Next(); break;
            case 100: if (Click("menu/file/recover-changes")) SeekPhase(2); break;
            case 110:
                Require(corruptDirtyVerified && retainedDirtyScene!.History.Entries[retainedDirtyScene.History.Position - 1].Id == ownedDirtyTransaction,
                    "Cleanup may undo only the exact retained dirty receiver gesture after refusal proof.");
                if (Click("toolbar/undo")) Next(); break;
            case 111:
                if (!RetainedCompensationDataEquals(retainedOriginalX) || retainedDirtyScene!.IsDirty) break;
                Require(retainedDirtyScene.History.Position == retainedCleanHistory!.Position
                    && retainedDirtyScene.History.Entries.Take(retainedCleanHistory.Entries.Count).SequenceEqual(retainedCleanHistory.Entries),
                    "Actual native toolbar Undo must restore original clean owner and applied history boundary.");
                AssertOriginalDisks();
                CorruptPayloadRecovery = new { actualDocumentRestoreActivation = Game.InspectCorruptRecoveryAction(),
                    comparison = corruptRefusalComparison,
                    noCanonicalPublication = true, originalOwnersHistoryNativeViewsAndDiskPreserved = true,
                    dirtyRetainedOwner = true, dirtyOwnerPreserved = corruptDirtyVerified, ownedTransactionUndone = true,
                    retainedDirtyEntry, retainedDirtyGeneration, retainedDirtyEntity, ownedDirtyTransaction,
                    originalPositionX = retainedOriginalX, refusedPositionX = EditedX,
                    genuineProducerSession = receipt!.SessionId, manifestSha256 = receipt.ManifestSha256 };
                Finish(true, "E08.recovery.corrupt-dirty PASS: real dirty native Position gesture/nonzero history preserved by actual corrupt Restore refusal; exact own toolbar Undo cleanup, original bytes and required outer retirement.");
                break;
        }
        return true;
    }
}

internal sealed partial class EditorGame
{
    internal sealed class CorruptRecoveryBaseline
    {
        internal required EditorSessionHistory History;
        internal required (EditorAuthoringState State, IEditorDocument Document, bool Dirty)[] Owners;
        internal required EditorDocumentEntry[] Entries;
        internal required string HistoryReceipt, Groups;
        internal required Dictionary<string, string> FileHashes;
        internal required (EntityHierarchyDocument Document, EngineViewport Viewport, object RuntimeScene)[] Views;
    }
    internal RecoveryDocumentActionReceipt? InspectCorruptRecoveryAction() => ownedRecovery?.LastDocumentAction;
    internal bool IsCorruptRecoveryReceiverSettled => !IsAuthoringPipelineUpdating && !IsRecoveryWorkspaceRestorePending && !RecoveryTransitionBusy && projectLoading == null;
    internal CorruptRecoveryBaseline CaptureCorruptRecoveryBaseline(IEnumerable<string> paths)
    {
        var history = sessionHistory ?? throw new InvalidOperationException("Receiver history is absent.");
        return new() { History = history,
            Owners = history.Registry.PublishedStates.Select(state => (state, state.Document, state.Document.IsDirty)).ToArray(),
            Entries = documents.Entries.ToArray(), HistoryReceipt = CaptureRecoveryAcceptanceHistory(),
            Groups = CorruptRecoveryGroupReceipt(),
            FileHashes = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase),
            Views = documents.Entries.Select(entry => entry.Document).OfType<EntityHierarchyDocument>()
                .Select(owner => { var view = VerificationDocumentViewport(owner) ?? throw new InvalidOperationException("Receiver actual native viewport is absent.");
                    return (owner, view, (object)(view.RuntimeScene ?? throw new InvalidOperationException("Receiver actual native scene is absent."))); }).ToArray() };
    }
    internal string CorruptRecoveryStabilitySignature(CorruptRecoveryBaseline baseline)
        => JsonSerializer.Serialize(new { baseline.Groups, baseline.HistoryReceipt,
            owners = baseline.Owners.Select(owner => new { owner.State.CanonicalPath, owner.State.Generation }),
            entries = baseline.Entries.Select(entry => entry.Id), baseline.FileHashes });

    internal object InspectCorruptRecoveryBaseline(CorruptRecoveryBaseline baseline) => new
    {
        sameHistoryOwner = ReferenceEquals(sessionHistory, baseline.History),
        beforeHistory = baseline.HistoryReceipt, afterHistory = CaptureRecoveryAcceptanceHistory(),
        beforeGroups = baseline.Groups, afterGroups = CorruptRecoveryGroupReceipt(),
        sameEntries = documents.Entries.SequenceEqual(baseline.Entries),
        beforeEntries = baseline.Entries.Select(entry => new { entry.Id, entry.Path }).ToArray(),
        afterEntries = documents.Entries.Select(entry => new { entry.Id, entry.Path }).ToArray(),
        beforeOwnerCount = baseline.Owners.Length, afterOwnerCount = baseline.History.Registry.PublishedStates.Count(),
        owners = baseline.Owners.Select(owner => new { owner.State.CanonicalPath, beforeGeneration = owner.State.Generation,
            afterGeneration = baseline.History.Registry.Find(owner.Document.Path)?.Generation, owner.State.IsPublished,
            sameDocument = ReferenceEquals(owner.State.Document, owner.Document),
            beforeDirty = owner.Dirty, afterDirty = owner.Document.IsDirty,
            sameState = ReferenceEquals(baseline.History.Registry.Find(owner.Document.Path), owner.State) }).ToArray(),
        views = baseline.Views.Select(view => new { view.Document.Path,
            sameViewport = ReferenceEquals(VerificationDocumentViewport(view.Document), view.Viewport),
            sameRuntimeScene = ReferenceEquals(view.Viewport.RuntimeScene, view.RuntimeScene) }).ToArray(),
        files = baseline.FileHashes.Select(file => new { path = file.Key, beforeSha256 = file.Value,
            afterSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))) }).ToArray(),
        IsRecoveryWorkspaceRestorePending, RecoveryTransitionBusy, IsAuthoringPipelineUpdating, projectLoading = projectLoading != null,
    };
    private string CorruptRecoveryGroupReceipt()
    {
        var presentation = CaptureRecoveryWorkspaceVerification();
        return JsonSerializer.Serialize(new { presentation.Groups, presentation.Documents, presentation.ActivePath });
    }
    internal void VerifyCorruptRecoveryBaseline(CorruptRecoveryBaseline baseline)
    {
        if (!ReferenceEquals(sessionHistory, baseline.History) || IsRecoveryWorkspaceRestorePending
            || CaptureRecoveryAcceptanceHistory() != baseline.HistoryReceipt || CorruptRecoveryGroupReceipt() != baseline.Groups
            || !documents.Entries.SequenceEqual(baseline.Entries)
            || baseline.History.Registry.PublishedStates.Count() != baseline.Owners.Length
            || baseline.Owners.Any(owner => !owner.State.IsPublished || !ReferenceEquals(owner.State.Document, owner.Document)
                || owner.Document.IsDirty != owner.Dirty
                || !ReferenceEquals(baseline.History.Registry.Find(owner.Document.Path), owner.State))
            || baseline.Views.Any(view => !ReferenceEquals(VerificationDocumentViewport(view.Document), view.Viewport)
                || !ReferenceEquals(view.Viewport.RuntimeScene, view.RuntimeScene))
            || baseline.FileHashes.Any(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))) != file.Value))
            throw new InvalidOperationException("Corrupt recovery changed prior native owner/view/history/group authority or left a transition pending.");
    }
}
