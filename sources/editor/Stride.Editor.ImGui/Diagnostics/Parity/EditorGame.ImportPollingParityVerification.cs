using Stride.Assets.Models;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ImportPollingParityVerification? importPollingParityVerification;
    internal bool StepNativePollingInputsParityVerification()
        => (importPollingParityVerification ??= new(this)).Step();
    internal object? ImportPollingParityVerificationEvidence => importPollingParityVerification?.Evidence;
    internal void CancelNativePollingInputsParityVerification() => importPollingParityVerification?.Release();

    // Observes actual production polls across frames. It never alters deadlines,
    // calls a polling pump directly, or hides a real eligible owner for a test.
    private sealed class ImportPollingParityVerification(EditorGame game)
    {
        private int phase, zeroOwnerPolls;
        private DateTime lastPollDeadline;
        private long capturesBefore;
        private long? zeroOwnerCaptureDelta, eligibleOwnerCaptureDelta;
        private EditorAuthoringState? openedOwner;
        private IDisposable? ownerLease;
        private string? assetId;
        internal object Evidence => new { phase, zeroOwnerPolls, assetId,
            zeroOwnerCaptureDelta,
            eligibleOwnerCaptureDelta,
            eligibleOwnerCount = game.importPollingEligibleOwnerCount,
            boundary = "Run inputs alone or before any import/material/procedural family. Two actual scheduled production polls with no eligible canonical owners, followed by a normal poll after resolving and retaining a real model graph. No forced polling timer or UI-input/compilation coverage." };
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private bool HasEligibleOwner() => game.authoring!.PublishedStates.Any(state => state.AssetId != null
            && (state.Document is AssetMetadataDocument metadata && (metadata.HasImportSettings || metadata.MaterialSettings != null)
                || state.Document is ProceduralAssetDocument { HasPendingCompilationEdits: false }));
        internal bool Step()
        {
            try
            {
                if (phase == 0)
                {
                    Require(game.args.Contains("--ui-test") && game.workspace != null && game.authoring != null,
                        "Import polling proof requires the real private inputs fixture.");
                    Require(!HasEligibleOwner(), "Run the optional inputs proof before opening import/material/procedural owners.");
                    game.VerifyNativePollingCertificateCacheParity();
                    Require(!HasEligibleOwner(), "Compositor cache verification must not manufacture an eligible import owner.");
                    capturesBefore = game.importPollingInputCaptureCount;
                    lastPollDeadline = game.importSettingsCheckAt;
                    zeroOwnerCaptureDelta = game.importPollingInputCaptureCount - capturesBefore;
                    phase = 1;
                }
                if (phase == 1)
                {
                    Require(!HasEligibleOwner(), "An unrelated eligible owner appeared during zero-owner observation.");
                    if (game.importSettingsCheckAt == lastPollDeadline) return false;
                    lastPollDeadline = game.importSettingsCheckAt;
                    zeroOwnerCaptureDelta = game.importPollingInputCaptureCount - capturesBefore;
                    Require(game.importPollingEligibleOwnerCount == 0 && game.importPollingInputCaptureCount == capturesBefore,
                        "A completed zero-owner poll must perform no session input capture.");
                    if (++zeroOwnerPolls < 2) return false;
                    var item = game.workspace!.AssetItems.FirstOrDefault(item => !item.Package.IsReadOnly && item.Asset is ModelAsset)
                        ?? throw new InvalidOperationException("The private native fixture has no editable imported model.");
                    Require(game.authoring!.Find(item.Id) == null, "The chosen model owner must first become eligible through the actual canonical resolver.");
                    var document = game.sessionHistory!.AssetGraphs.Resolve?.Invoke(item.Id) as AssetMetadataDocument
                        ?? throw new InvalidOperationException("The model did not resolve its actual native metadata graph.");
                    openedOwner = game.authoring.Find(item.Id);
                    Require(openedOwner != null && ReferenceEquals(openedOwner.Document, document) && document.HasImportSettings,
                        "The canonical model must expose real import settings.");
                    ownerLease = openedOwner!.AcquireView();
                    assetId = item.Id.ToString();
                    capturesBefore = game.importPollingInputCaptureCount;
                    phase = 2;
                }
                if (phase == 2)
                {
                    if (game.importSettingsCheckAt == lastPollDeadline) return false;
                    lastPollDeadline = game.importSettingsCheckAt;
                    eligibleOwnerCaptureDelta = game.importPollingInputCaptureCount - capturesBefore;
                    Require(game.importPollingEligibleOwnerCount >= 1 && game.importPollingInputCaptureCount - capturesBefore == 1,
                        "A normal poll with the real model owner must capture session inputs exactly once.");
                    phase = 3;
                    Release();
                }
                return phase == 3;
            }
            catch { Release(); throw; }
        }
        internal void Release()
        {
            ownerLease?.Dispose(); ownerLease = null;
            if (openedOwner is { IsDisposed: false } owner)
            {
                game.authoring!.ReleaseIfUnused(owner);
                // Registry collection honors actual history/view leases and
                // dirty state; no forced graph disposal or history clearing.
                game.authoring.CollectUnused();
                openedOwner = null;
            }
        }
    }
}
