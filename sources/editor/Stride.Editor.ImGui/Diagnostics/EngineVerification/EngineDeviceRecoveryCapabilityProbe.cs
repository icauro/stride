using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineDeviceRecoveryCapabilityProbe : EngineUiCapabilityProbe
{
    private SceneDocument? owner;
    private EngineViewport? oldView;
    private GraphicsDevice? oldDevice;
    private GraphicsContext? oldContext;
    private Texture? oldTarget;
    private object? workspaceOwner, historyOwner, contentOwner, oldRuntime;
    private Guid[] entities = [], transactions = [];
    private Guid selectedId;
    private Guid? parentId;
    private string? originalName, changedName;
    private Matrix camera;
    private string? compiledScene;
    private EngineOutputSample? baseline, previous;
    private EngineOutputDifference? restoredDifference;
    private int stable;
    private long sampledRevision = -1;
    private double nextSample;
    private bool renameDefocused;
    private object? historyBefore;
    protected override string Scenario => "E06.recovery";
    protected override double PhaseTimeoutSeconds => Phase <= 2 || Phase == 9 ? 180 : 45;
    protected override object Evidence => new
    {
        request = "Controlled local host request at BeginDraw; actual native device/context replacement; no global GPU reset.",
        physicalDriverLossDetectionCovered = false,
        started = Game.VerificationDeviceRecoveryStarted, pending = Game.VerificationDeviceRecoveryPending,
        recoveryError = Game.VerificationDeviceRecoveryError?.ToString(),
        oldDeviceDisposed = oldDevice?.IsDisposed, oldTargetDisposed = oldTarget?.IsDisposed,
        distinctDevice = oldDevice != null && !ReferenceEquals(oldDevice, Game.GraphicsDevice),
        distinctContext = oldContext != null && !ReferenceEquals(oldContext, Game.GraphicsContext),
        distinctNativeWorld = oldRuntime != null && owner != null
            && !ReferenceEquals(oldRuntime, Game.VerificationDocumentViewport(owner)?.RuntimeScene),
        compiledScene, selectedId, entities, transactions, originalName, changedName, restoredDifference,
        observedDocumentSameOwner = owner != null && ReferenceEquals(Game.Document, owner),
        observedSelectedId = owner?.Selected?.Id, observedSelectedName = owner?.Selected?.Name,
        observedEntities = owner?.Asset.Hierarchy.Parts.Values.Select(part => part.Entity.Id).Order().ToArray(),
        observedTransactions = Game.VerificationHistoryTransactions,
        historyBefore, historyAfter = Game.VerificationRecoveryHistoryEntries,
        dirty = owner?.IsDirty, canUndo = owner?.CanUndo, canRedo = owner?.CanRedo,
        gaps = new[] { "Physical driver-loss detection, terminal native replacement failure, other graphics APIs and all preview-family reconstruction are separate cases." }
    };
    internal EngineDeviceRecoveryCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                if (Click("entity/Sphere")) Next(); break;
            case 3:
                if (Game.Document?.Selected?.Name != "Sphere") break;
                if (owner == null)
                {
                    owner = Game.Document; selectedId = owner.Selected.Id;
                    parentId = owner.Selected.Transform.Parent?.Entity.Id;
                    originalName = owner.Selected.Name; changedName = "Capability recovery sphere";
                    Require(!owner.IsDirty && owner.CanEditSelected, "Recovery fixture must start clean and editable.");
                }
                if (!Game.Controls.Centers.ContainsKey("inspector/name"))
                {
                    Click("tooltab/editor.properties");
                    break;
                }
                if (Click("inspector/name")) Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 5:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in changedName!) io.AddInputCharacter(c);
                Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 7: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 8:
                if (owner!.Selected?.Name != changedName) break;
                if (!renameDefocused)
                {
                    var tab = Game.VerificationDocumentId(owner);
                    if (tab != null && Click("document-tab/" + tab.Value)) renameDefocused = true;
                    break;
                }
                if (!EnsureScenePresented()) break;
                Require(owner.IsDirty && owner.CanUndo, "A real UI edit must supply dirty authoring/history to preserve.");
                Game.AssertCompiledSceneVerification();
                Game.AssertRuntimeEntityVerification(selectedId, parentId);
                if (!StableOutput(out var before)) break;
                baseline = before; oldView = Game.VerificationDocumentViewport(owner);
                Require(oldView?.RuntimeScene != null && oldView.VerificationCompositorOwner != null,
                    "Recovery must start with a real compiled scene and authored compositor.");
                oldDevice = Game.GraphicsDevice; oldContext = Game.GraphicsContext; oldTarget = oldView!.Target;
                oldRuntime = oldView.RuntimeScene;
                camera = oldView.Camera.Component.ViewMatrix; compiledScene = oldView.CompiledSceneUrl;
                workspaceOwner = Game.VerificationWorkspaceOwner; historyOwner = Game.VerificationHistoryOwner;
                contentOwner = Game.VerificationCompiledProjectOwner;
                entities = owner.Asset.Hierarchy.Parts.Values.Select(part => part.Entity.Id).Order().ToArray();
                transactions = Game.VerificationHistoryTransactions;
                historyBefore = Game.VerificationRecoveryHistoryEntries;
                Require(transactions.Length > 0 && oldTarget != null, "Actual undo transactions and scene GPU resources are required.");
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E06-recovery-before.png"));
                Game.QueueVerificationDeviceRecovery(); previous = null; stable = 0; sampledRevision = -1; Next(); break;
            case 9:
                if (!Game.VerificationDeviceRecoveryStarted || Game.VerificationDeviceRecoveryPending) break;
                Require(Game.VerificationDeviceRecoveryError == null && Game.GraphicsDevice.GraphicsDeviceStatus == GraphicsDeviceStatus.Normal,
                    "The real recovery must complete without native replacement failure.");
                Require(oldDevice!.IsDisposed && oldTarget!.IsDisposed && !ReferenceEquals(oldDevice, Game.GraphicsDevice)
                    && !ReferenceEquals(oldContext, Game.GraphicsContext), "Old device/target must retire and actual device/context must be replaced.");
                Require(ReferenceEquals(Game.VerificationWorkspaceOwner, workspaceOwner)
                    && ReferenceEquals(Game.VerificationHistoryOwner, historyOwner)
                    && ReferenceEquals(Game.VerificationCompiledProjectOwner, contentOwner), "Recovery must retain authored session/history and installed compiled generation.");
                Require(ReferenceEquals(Game.Document, owner), "Recovery must retain the exact active authored document owner.");
                Require(owner!.Selected?.Id == selectedId && owner.Selected.Name == changedName,
                    "Recovery must retain exact authored selection identity and the unsaved edited name.");
                Require(owner.IsDirty && owner.CanUndo, "Recovery must retain dirty state and usable undo.");
                Require(entities.SequenceEqual(owner.Asset.Hierarchy.Parts.Values.Select(part => part.Entity.Id).Order()),
                    "Recovery must retain every canonical authored entity identity.");
                Require(transactions.SequenceEqual(Game.VerificationHistoryTransactions),
                    "Recovery must retain the exact ordered native history transaction identities.");
                if (!EnsureScenePresented()) break;
                var current = Game.VerificationDocumentViewport(owner);
                Require(current != null && !ReferenceEquals(current, oldView) && !ReferenceEquals(current.Target, oldTarget)
                    && current.RuntimeScene != null && !ReferenceEquals(current.RuntimeScene, oldRuntime)
                    && current.CompiledSceneUrl == compiledScene && current.VerificationCompositorOwner != null
                    && current.Camera.Component.ViewMatrix == camera, "A distinct real native viewport must restore compiled scene identity, compositor and camera.");
                Game.AssertRuntimeEntityVerification(selectedId, parentId);
                Game.AssertCompiledSceneVerification();
                if (!StableOutput(out var after)) break;
                restoredDifference = baseline!.Compare(after, 0, 0, baseline.Width, baseline.Height);
                if (restoredDifference.Value.ChangedFraction > .005) break;
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E06-recovery-after.png"));
                if (Click("toolbar/undo")) Next(); break;
            case 10:
                if (owner!.Selected?.Name != originalName) break;
                Require(!owner.IsDirty && owner.CanRedo, "The preserved pre-recovery transaction must still undo to the clean savepoint.");
                Game.AssertRuntimeEntityVerification(selectedId, parentId);
                Finish(true, "E06.recovery PASS: controlled local request replaced actual D3D11 device/context, retired old target, restored compiled scene/camera/GPU output and preserved dirty authored identities and working undo history.");
                break;
        }
    }
    private bool StableOutput(out EngineOutputSample sample)
    {
        sample = null!;
        if (Clock.Elapsed.TotalSeconds < nextSample || Game.VerificationSceneRenderRevision <= sampledRevision) return false;
        nextSample = Clock.Elapsed.TotalSeconds + .25; sampledRevision = Game.VerificationSceneRenderRevision;
        sample = Game.ReadSceneVerificationOutput();
        var same = previous != null && previous.Width == sample.Width && previous.Height == sample.Height
            && previous.Format == sample.Format && previous.Compare(sample, 0, 0, sample.Width, sample.Height).ChangedFraction <= .001;
        previous = sample; stable = same ? stable + 1 : 0; return stable >= 3;
    }
}
