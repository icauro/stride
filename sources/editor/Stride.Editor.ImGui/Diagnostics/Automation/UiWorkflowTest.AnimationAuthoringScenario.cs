using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal sealed record AnimationAuthoringVerificationState(string? ExpectedId, string? ExpectedPath, string? ActualName,
    string? ActiveId, string? ActivePath, bool? CropEnabled, bool? RootMotion, string? RepeatMode, TimeSpan? Start, TimeSpan? End,
    string? Source, string? SkeletonId, string? PreviewModelId, bool Dirty, bool CanUndo, bool CanRedo);

internal sealed partial class EditorGame
{
    internal AnimationAuthoringVerificationState InspectAnimationAuthoringVerification(AssetId id)
    {
        var expected = workspace?.Assets.FirstOrDefault(entry => entry.Id == id);
        // Imported animation settings belong to the canonical selected-asset
        // Property grid command owner; this asset has no separate edit tab.
        var owner = PropertyGridCommandDocument as AssetMetadataDocument;
        var asset = owner?.AuthoredAsset as AnimationAsset;
        return new(expected?.Id.ToString(), expected?.Path, expected?.Name, asset?.Id.ToString(), owner?.Path,
            asset?.ClipDuration.Enabled, asset?.RootMotion, asset?.RepeatMode.ToString(), asset?.ClipDuration.StartAnimationTime,
            asset?.ClipDuration.EndAnimationTime, asset?.Source.ToString(),
            asset == null ? null : AttachedReferenceManager.GetAttachedReference(asset.Skeleton)?.Id.ToString(),
            asset == null ? null : AttachedReferenceManager.GetAttachedReference(asset.PreviewModel)?.Id.ToString(),
            owner?.IsDirty ?? false, owner?.CanUndo ?? false, owner?.CanRedo ?? false);
    }
}

internal sealed partial class UiWorkflowTest
{
    private PreviewFamilyFixtureVerification? animationAuthoringFixture;
    private AnimationAuthoringVerificationState? animationOriginal;
    private const string AnimationRootMotionControl = "asset-settings/Asset.RootMotion";
    private bool animationSaveGesturePending;

    internal void PrepareAnimationAuthoringScenarioFixture()
        => animationAuthoringFixture = game.PreparePreviewFamilyFixture("animation");

    private AnimationAuthoringVerificationState AnimationAuthoringState()
        => game.InspectAnimationAuthoringVerification(animationAuthoringFixture!.AssetId);

    private void UpdateAnimationAuthoringScenario()
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        switch (stage)
        {
            case 11400:
                Assert(animationAuthoringFixture != null && AnimationAuthoringState().ExpectedId == animationAuthoringFixture.AssetId.ToString(),
                    "U06 animation requires the exact native compiled/private fixture asset ID");
                if (!game.VerificationFamilyBrowserRecursive)
                {
                    var control = game.Controls.Centers.ContainsKey("asset/options/recursive") ? "asset/options/recursive"
                        : game.Controls.Centers.ContainsKey("asset/options/display") ? "asset/options/display" : "asset/options";
                    if (!game.Controls.Centers.ContainsKey(control)) { TryClickToolTab("assets.browser"); break; }
                    Click(control); break;
                }
                if (Click("asset/filter")) Next(); break;
            case 11401: io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.ModCtrl, true); io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.A, true); Next(); break;
            case 11402:
                io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.ModCtrl, false); io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.A, false);
                foreach (var character in animationAuthoringFixture!.AssetName) io.AddInputCharacter(character);
                Next(); break;
            case 11403: io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.Enter, true); Next(); break;
            case 11404: io.AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.Enter, false); Next(); break;
            case 11405:
                var controlId = game.VerificationFamilyAssetControl(animationAuthoringFixture!.AssetId);
                Assert(controlId != null, "Canonical animation asset must exist in the actual loaded workspace");
                if (Click(controlId!)) Next(); break;
            case 11406:
                var state = AnimationAuthoringState();
                if (state.ActiveId != state.ExpectedId || !SameAssetPath(state.ActivePath, state.ExpectedPath))
                { TryClickToolTab("editor.properties"); break; }
                animationOriginal ??= state;
                Assert(state.RootMotion.HasValue && !state.Dirty && state.SkeletonId != null && state.PreviewModelId != null,
                    "Animation inspector must own the clean real imported clip, skeleton and preview-model references");
                if (ClickInspectorControl(AnimationRootMotionControl)) Next(); break;
            case 11407:
                var edited = AnimationAuthoringState();
                Assert(edited.RootMotion == !animationOriginal!.RootMotion!.Value && edited.Dirty && edited.CanUndo,
                    "Actual animation root-motion checkbox must create an undoable authored change");
                AssertAnimationUnrelatedFields(edited);
                if (Click("toolbar/undo")) Next(); break;
            case 11408:
                var undone = AnimationAuthoringState();
                Assert(undone.RootMotion == animationOriginal!.RootMotion && !undone.Dirty && undone.CanRedo,
                    "Undo must restore the animation root-motion value and clean save point");
                AssertAnimationUnrelatedFields(undone);
                if (Click("toolbar/redo")) Next(); break;
            case 11409:
                var redone = AnimationAuthoringState();
                Assert(redone.RootMotion == !animationOriginal!.RootMotion!.Value && redone.Dirty && redone.CanUndo && !redone.CanRedo,
                    "Redo must restore the exact animation root-motion change");
                AssertAnimationUnrelatedFields(redone);
                checks.Add("Animation root-motion import setting checkbox, canonical owner, undo/redo and unchanged source/dependencies");
                if (Click("toolbar/save")) { animationSaveGesturePending = true; Next(); } break;
            case 11410:
                var saved = AnimationAuthoringState();
                if (saved.Dirty)
                {
                    // A preview rebuild can disable commands between the prior
                    // frame's enabled geometry and the delivered mouse press.
                    // Release the real gesture, then retry only the still-dirty
                    // document through the actual enabled toolbar action.
                    if (animationSaveGesturePending)
                    {
                        io.AddMouseButtonEvent(0, false); animationSaveGesturePending = false; wait = 2; break;
                    }
                    if (Click("toolbar/save")) { animationSaveGesturePending = true; wait = 2; }
                    break;
                }
                Assert(saved.ActiveId == saved.ExpectedId && saved.RootMotion == !animationOriginal!.RootMotion!.Value,
                    "Save must retain canonical animation identity and root-motion value");
                AssertAnimationUnrelatedFields(saved);
                var disk = AssetFileSerializer.Load<AnimationAsset>(saved.ExpectedPath!).Asset;
                Assert(disk.Id.ToString() == saved.ExpectedId && disk.RootMotion == saved.RootMotion && disk.ClipDuration.Enabled == animationOriginal!.CropEnabled
                    && disk.ClipDuration.StartAnimationTime == animationOriginal.Start
                    && disk.ClipDuration.EndAnimationTime == animationOriginal.End
                    && disk.RepeatMode.ToString() == animationOriginal.RepeatMode && disk.Source.ToString() == animationOriginal.Source
                    && AttachedReferenceManager.GetAttachedReference(disk.Skeleton)?.Id.ToString() == animationOriginal.SkeletonId
                    && AttachedReferenceManager.GetAttachedReference(disk.PreviewModel)?.Id.ToString() == animationOriginal.PreviewModelId,
                    "Independent native file load must retain animation identity, root-motion value, range, repeat hint and exact source/dependency IDs");
                checks.Add("Independent AnimationAsset deserialization verifies saved root-motion value and unchanged source/skeleton/model references");
                CompleteIndependentSubset(); break;
        }
    }

    private void AssertAnimationUnrelatedFields(AnimationAuthoringVerificationState current)
        => Assert(current.ActiveId == animationOriginal!.ActiveId && current.RepeatMode == animationOriginal.RepeatMode
            && current.CropEnabled == animationOriginal.CropEnabled
            && current.Start == animationOriginal.Start && current.End == animationOriginal.End
            && current.Source == animationOriginal.Source && current.SkeletonId == animationOriginal.SkeletonId
            && current.PreviewModelId == animationOriginal.PreviewModelId,
            "Root-motion edit must preserve the actual animation range, repeat hint, source, skeleton and preview-model references");
}
