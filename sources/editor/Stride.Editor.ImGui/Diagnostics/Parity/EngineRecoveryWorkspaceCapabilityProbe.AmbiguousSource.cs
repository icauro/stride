using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineRecoveryWorkspaceCapabilityProbe
{
    private EditorGame.CorruptRecoveryBaseline? ambiguousSourceBaseline;
    private RecoveryAmbiguousSourceFixture? ambiguousSourceFixture;
    private object? ambiguousNativeAuthority, AmbiguousSourceRecovery;
    private int ambiguousRestoreFrame;
    private string? ambiguousStableSignature;
    private int ambiguousStableFrames;
    private bool StepAmbiguousSourceScenario()
    {
        if (producer || !Environment.GetCommandLineArgs().Contains("--recovery-ambiguous-external-source")) return false;
        switch (Phase)
        {
            case 0:
                ambiguousSourceFixture ??= Game.PrepareRecoveryAmbiguousSourceReceiverFixture(receipt!);
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented() || !Game.IsCorruptRecoveryReceiverSettled) break;
                ambiguousNativeAuthority ??= Game.VerifyRecoveryAmbiguousSourceAuthority(receipt!.GeneratedReadOnlySources!.ExternalReadOnlyDescriptor);
                Next(); break;
            case 3:
                var button = Game.InspectRecoveryWorkspaceButton(receipt!.SessionId);
                if (button == null || !button.Visible || button.Frame < Gui.GetFrameCount() - 1)
                { ClickRecovery("recovery/workspace/" + receipt.SessionId, 1); break; }
                Require(!button.Disabled && button.ItemId != 0,
                    "Actual workspace Restore must be enabled before its real ownership preflight refusal.");
                if (!Game.IsCorruptRecoveryReceiverSettled) { ambiguousStableFrames = 0; break; }
                var files = receipt.OriginalDisks.Select(disk => disk.Path).Concat(receipt.Sources.Select(source => source.Membership.Descriptor.Path))
                    .Concat(Game.RecoveryAmbiguousReceiverFiles);
                var baseline = Game.CaptureCorruptRecoveryBaseline(files);
                var signature = Game.CorruptRecoveryStabilitySignature(baseline);
                if (signature != ambiguousStableSignature) { ambiguousStableSignature = signature; ambiguousStableFrames = 0; }
                if (++ambiguousStableFrames < 8) break;
                ambiguousSourceBaseline = baseline;
                if (ClickRecovery("recovery/workspace/" + receipt.SessionId, 1))
                { ambiguousRestoreFrame = Gui.GetFrameCount(); Next(); }
                break;
            case 4:
                var action = Game.InspectRecoveryWorkspaceAction();
                if (action == null || action.Frame < ambiguousRestoreFrame) break;
                Require(action.Session == receipt!.SessionId && action.ItemId != 0 && action.HostRestoreInvoked
                    && !action.HostRestoreReturned && action.ErrorType == typeof(InvalidOperationException).FullName
                    && !string.IsNullOrWhiteSpace(action.Error),
                    "Actual native Restore must call production ownership preflight and refuse before publishing the old unowned source.");
                Require(Hash(receipt.ManifestPath) == receipt.ManifestSha256 && Hash(ambiguousSourceFixture!.SourcePath) == ambiguousSourceFixture.SourceHash,
                    "Actual receiver refusal must preserve the immutable producer checkpoint and exact source bytes.");
                Game.VerifyCorruptRecoveryBaseline(ambiguousSourceBaseline!);
                AmbiguousSourceRecovery = new { actualWorkspaceRestoreActivation = action, nativeAuthority = ambiguousNativeAuthority,
                    comparison = Game.InspectCorruptRecoveryBaseline(ambiguousSourceBaseline!),
                    originalDescriptor = receipt.GeneratedReadOnlySources!.ExternalReadOnlyDescriptor,
                    actualTwoLoadedProjects = true, receiverPreflightRefused = true, noCanonicalPublication = true,
                    originalOwnersHistoryNativeViewsAndDiskPreserved = true, genuineProducerSession = receipt.SessionId,
                    manifestSha256 = receipt.ManifestSha256, fixture = ambiguousSourceFixture };
                if (Click("recovery/close")) Next(); break;
            case 5:
                Game.VerifyCorruptRecoveryBaseline(ambiguousSourceBaseline!);
                Require(Hash(receipt!.ManifestPath) == receipt.ManifestSha256, "Closing the refusal dialog must not change the original checkpoint.");
                Finish(true, "Actual producer descriptor refused by native receiver Restore after two genuine evaluated SDK project owners load; original native owners/world/history/files retained, normal terminal required.");
                break;
        }
        return true;
    }
}
