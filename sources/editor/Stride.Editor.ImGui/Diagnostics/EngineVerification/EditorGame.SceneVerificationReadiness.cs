using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal bool VerificationSceneHasRuntime => InspectionViewport is { RuntimeScene: not null, UsesAuthoredCompositor: true, RuntimeSourceAvailable: true };
    internal bool VerificationScenePresented => InspectionViewport is { Visible: true, Target: not null } && EngineDrawCalls > 0;
    internal Matrix VerificationSceneCamera => InspectionViewport?.Camera.Component.ViewMatrix
        ?? throw new InvalidOperationException("The actual document viewport camera is unavailable.");
    internal int VerificationPickingReadbacks => InspectionViewport?.Picker?.ReadbackCount ?? 0;
    internal bool VerificationPickingResultPending => InspectionViewport?.Picker?.HasResult == true;
    internal bool VerificationPickingCameraCurrent => InspectionViewport is { Picker: { } picker, Target: { } target } viewport
        && picker.DisplayedViewProjection == viewport.Camera.Component.ViewProjectionMatrix
        && picker.DisplayedSize.X == target.Width && picker.DisplayedSize.Y == target.Height;
    internal int VerificationCancelledPickRequests => InspectionViewport?.Picker?.CancelledRequests ?? 0;
    internal int VerificationSelectedRuntimeModels => InspectionViewport?.SelectedModelCount ?? 0;
    internal int VerificationPreparedSelectionNodes => InspectionViewport?.PreparedSelectionNodes ?? 0;
    internal int VerificationSelectionDrawCalls => InspectionViewport?.SelectionRenderer?.LastDrawCalls ?? 0;
    internal Vector2 VerificationEntityScreenPosition(string name) => InspectionViewport?.EntityScreenPosition(name)
        ?? throw new InvalidOperationException("The actual document viewport is unavailable.");

    // Record the actual native docking tab geometry; activation remains mouse input.
    internal unsafe bool RecordVerificationSceneDockTab()
    {
        Controls.Centers.Remove("engine/scene-tab");
        Controls.Viewports.Remove("engine/scene-tab");
        Controls.NativeIDs.Remove("engine/scene-tab");
        Controls.NativeFrames.Remove("engine/scene-tab");
        if (documentGroupPanel == null || document == null) return false;
        var sceneEntry = documents.Find(document);
        if (sceneEntry != null && Controls.Centers.TryGetValue("document-tab/" + sceneEntry.Id, out var sceneTabCenter))
        {
            // The outer dock window may already be selected while a different
            // document inside it is active. Use the actual recorded scene tab.
            Controls.Centers["engine/scene-tab"] = sceneTabCenter;
            var source = "document-tab/" + sceneEntry.Id;
            if (Controls.Viewports.TryGetValue(source, out var viewport)) Controls.Viewports["engine/scene-tab"] = viewport;
            if (Controls.NativeIDs.TryGetValue(source, out var id)) Controls.NativeIDs["engine/scene-tab"] = id;
            if (Controls.NativeFrames.TryGetValue(source, out var frame)) Controls.NativeFrames["engine/scene-tab"] = frame;
            return true;
        }
        var group = documentGroupPanel.Model.Groups.FirstOrDefault(candidate => candidate.Items.Any(entry => ReferenceEquals(entry.Document, document)));
        if (group == null) return false;
        var window = ImGuiP.FindWindowByName(group.WindowName);
        if (window.IsNull || !window.DockIsActive || window.DockNode.IsNull) return false;
        var bar = window.DockNode.TabBar;
        if (bar.IsNull || bar.WantLayout) return false;
        var rect = window.DC.DockTabItemRect;
        var minimum = System.Numerics.Vector2.Max(rect.Min, bar.BarRect.Min);
        var maximum = System.Numerics.Vector2.Min(rect.Max, bar.BarRect.Max);
        minimum.X = Math.Max(minimum.X, bar.ScrollingRectMinX);
        maximum.X = Math.Min(maximum.X, bar.ScrollingRectMaxX);
        if (!float.IsFinite(minimum.X) || !float.IsFinite(maximum.X) || maximum.X - minimum.X < Gui.GetFrameHeight() * 2 || maximum.Y <= minimum.Y) return false;
        Controls.Centers["engine/scene-tab"] = (minimum + maximum) * .5f;
        if (!window.Viewport.IsNull) Controls.Viewports["engine/scene-tab"] = window.Viewport.Handle->ImGuiViewport.ID;
        return true;
    }
}
