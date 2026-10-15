using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Synthetic ImGui pointer gesture over an actual Stride gizmo, not direct authored mutation.</summary>
internal sealed class EngineGizmoCapabilityProbe : EngineUiCapabilityProbe
{
    private EntityHierarchyDocument? owner;
    private Guid targetId;
    private TransformState before, after;
    private Matrix camera;
    private int historyPosition, candidate, motionStep;
    private bool scanning, focusSent;
    private NVector2 handle;
    private string? axes;
    protected override string Scenario => "E02.gizmo";
    protected override object Evidence => new
    {
        targetId, candidate, motionStep, axes, historyPosition,
        captured = Game.VerificationTransformGizmos?.Captured,
        hovered = Game.VerificationTransformGizmos?.VerificationHandleHovered,
        mode = Game.VerificationTransformGizmos?.Mode,
        input = "Synthetic ImGui pointer/key events; actual native transformation hit-test and runtime gesture",
        gaps = new[] { "Rotation, scale, snapping, cancellation and OS physical pointer coverage remain separate." },
    };
    internal EngineGizmoCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void CleanInput() => Gui.GetIO().AddKeyEvent(ImGuiKey.W, false);
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document != null && EnsureScenePresented() && Click("entity/Sphere")) Next();
                break;
            case 3:
                if (Game.Document?.Selected?.Name != "Sphere") break;
                if (!focusSent)
                {
                    if (Game.RecordVerificationSceneDockTab() && Click("engine/scene-tab")) focusSent = true;
                    break;
                }
                owner = Game.Document; targetId = owner.Selected.Id;
                before = TransformState.Read(owner.Selected); camera = Game.VerificationSceneCamera;
                historyPosition = owner.History.Position;
                var center = Game.VerificationEntityScreenPosition("Sphere");
                if (!Aim("gizmo-mode-focus", Game.SceneImageMinimum + new NVector2(center.X, center.Y) * Game.SceneImageSize)) break;
                io.AddKeyEvent(ImGuiKey.W, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.W, false);
                Require(Game.VerificationTransformGizmos is { Mode: 0, Captured: false }, "Actual translation shortcut must select idle translation gizmo");
                Next(); break;
            case 5:
                RequireOwner();
                if (scanning && Game.VerificationTransformGizmos?.VerificationHandleHovered == true)
                {
                    handle = io.MousePos; axes = Game.VerificationTransformGizmos.VerificationHandleAxes;
                    io.AddMouseButtonEvent(0, true); Next(); break;
                }
                Require(candidate < 192, "No actual transform handle hit found in bounded viewport scan");
                var normalized = Game.VerificationEntityScreenPosition("Sphere");
                var anchor = Game.SceneImageMinimum + new NVector2(normalized.X, normalized.Y) * Game.SceneImageSize;
                var angle = candidate % 24 * MathF.PI / 12;
                var radius = 8 + candidate / 24 * 8;
                candidate++;
                var point = anchor + new NVector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                if (point.X <= Game.SceneImageMinimum.X || point.Y <= Game.SceneImageMinimum.Y
                    || point.X >= Game.SceneImageMinimum.X + Game.SceneImageSize.X || point.Y >= Game.SceneImageMinimum.Y + Game.SceneImageSize.Y) break;
                io.AddMousePosEvent(point.X, point.Y); scanning = true; break;
            case 6:
                RequireOwner();
                Require(Game.VerificationTransformGizmos?.Captured == true, "Press on native hit-tested handle must acquire gesture ownership");
                motionStep++;
                io.AddMousePosEvent(handle.X + motionStep * 12, handle.Y + motionStep * 9);
                if (motionStep == 3) Next();
                break;
            case 7:
                io.AddMouseButtonEvent(0, false); Next(); break;
            case 8:
                RequireOwner();
                if (Game.VerificationTransformGizmos?.Captured == true) break;
                after = TransformState.Read(owner!.Asset.Hierarchy.Parts[targetId].Entity);
                Require(after != before && owner.IsDirty && owner.History.Position == historyPosition + 1,
                    "Actual runtime drag must commit a changed authored transform as one transaction");
                Require(Game.VerificationSceneCamera == camera, "Transform gesture must preserve independent camera");
                Game.AssertRuntimeEntityVerification(targetId, null);
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "gizmo-translated.png"));
                if (Click("toolbar/undo")) Next(); break;
            case 9:
                RequireOwner();
                if (owner!.History.Position != historyPosition) break;
                Require(TransformState.Read(owner.Asset.Hierarchy.Parts[targetId].Entity) == before && !owner.IsDirty,
                    "One UI undo must restore whole runtime gesture and initial clean save point");
                Game.AssertRuntimeEntityVerification(targetId, null);
                if (Click("toolbar/redo")) Next(); break;
            case 10:
                RequireOwner();
                if (owner!.History.Position != historyPosition + 1) break;
                Require(TransformState.Read(owner.Asset.Hierarchy.Parts[targetId].Entity) == after && owner.IsDirty
                    && Game.VerificationSceneCamera == camera, "UI redo must restore exact authored gesture without camera mutation");
                Game.AssertRuntimeEntityVerification(targetId, null);
                Finish(true, "E02 translation gizmo PASS: actual handle hit, synthetic pointer drag, runtime/authored synchronization, one transaction and UI undo/redo.");
                break;
        }
    }
    private void RequireOwner() => Require(ReferenceEquals(Game.Document, owner) && owner?.Selected?.Id == targetId,
        "Gizmo verification must retain exact document and selected entity owner");
}
