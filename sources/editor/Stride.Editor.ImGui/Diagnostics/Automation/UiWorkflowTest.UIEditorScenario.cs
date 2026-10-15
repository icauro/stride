using Hexa.NET.ImGui;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private string? uiOriginalName, uiOriginalAssetId;
    private int uiOriginalCount;
    private const string UiEditedName = "U06 button – 星";

    private void UpdateUIEditorScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 11800:
                Assert(uiAuthoredFixturePath != null, "UI authored fixture must be prepared before UI loading");
                if (Click("asset/filter")) Next(); break;
            case 11801: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11802:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in UiFixtureName) io.AddInputCharacter(c);
                Next(); break;
            case 11803: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11804: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11805: if (Click("asset/" + UiFixtureName)) Next(); break;
            case 11806: if (Click("asset/edit")) Next(); break;
            case 11807:
                var opened = game.InspectUiAuthoredName();
                if (!SameAssetPath(opened.Path, uiAuthoredFixturePath)) break;
                Assert(opened.Name == "U06Button" && opened.ItemIdentity == UiFixtureButtonId.ToString() && !opened.Dirty && opened.Count == 2,
                    "UI document must own the actual fixture root/button hierarchy");
                uiOriginalName = opened.Name; uiOriginalAssetId = opened.AssetId; uiOriginalCount = opened.Count;
                if (Click("ui/expand")) Next(); break;
            case 11808:
                var childControl = "ui/element/" + UiFixtureButtonId;
                if (!game.Controls.Centers.ContainsKey(childControl)
                    && game.Controls.Centers.TryGetValue("ui/hierarchy/body", out var hierarchyBody))
                {
                    io.AddMousePosEvent(hierarchyBody.X, hierarchyBody.Y); io.AddMouseWheelEvent(0, -1);
                    RecordAction("scroll-ui-hierarchy", childControl); wait = 2; break;
                }
                if (Click(childControl)) Next(); break;
            case 11809: if (PressRightSemanticControl("ui/element/" + UiFixtureButtonId)) Next(); break;
            case 11810: io.AddMouseButtonEvent(1, false); Next(); break;
            case 11811: if (Click("ui/menu/rename")) Next(); break;
            case 11812: if (Click("ui/rename/" + UiFixtureButtonId)) Next(); break;
            case 11813: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11814:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in UiEditedName) io.AddInputCharacter(c);
                Next(); break;
            case 11815: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11816: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11817: if (Click("toolbar/undo")) Next(); break;
            case 11818:
                var undone = game.InspectUiAuthoredName();
                Assert(undone.Name == uiOriginalName && !undone.Dirty && undone.CanRedo,
                    "UI rename undo must restore the authored value and original save point");
                if (Click("toolbar/redo")) Next(); break;
            case 11819:
                var redone = game.InspectUiAuthoredName();
                Assert(redone.Name == UiEditedName && redone.Dirty && redone.CanUndo && redone.ItemIdentity == UiFixtureButtonId.ToString()
                    && redone.AssetId == uiOriginalAssetId && redone.Count == uiOriginalCount,
                    "UI redo must preserve asset/element IDs and complete native hierarchy");
                if (Click("toolbar/save")) Next(); break;
            case 11820:
                var saved = game.InspectUiAuthoredName();
                if (saved.Dirty) break;
                var disk = AssetFileSerializer.Load<UIPageAsset>(uiAuthoredFixturePath!).Asset;
                var button = disk.Hierarchy.Parts[UiFixtureButtonId].UIElement;
                Assert(disk.Id.ToString() == uiOriginalAssetId && disk.Hierarchy.Parts.Count == uiOriginalCount
                    && button.Name == UiEditedName && disk.Hierarchy.RootParts.Count == 1
                    && button.VisualParent?.Id == disk.Hierarchy.RootParts[0].Id,
                    "Independent native UI asset reload must preserve renamed button identity and root relationship");
                checks.Add("UI tree context Rename/input/undo/redo/save independently preserves authored native hierarchy");
                CompleteIndependentSubset(); break;
        }
    }

    private bool PressRightSemanticControl(string id)
    {
        if (!game.Controls.Centers.TryGetValue(id, out var point)) { lastMissingControl = id; return false; }
        var io = Gui.GetIO();
        if (pendingClickId != "right/" + id || pendingClickPoint != point || System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2f)
        {
            pendingClickId = "right/" + id; pendingClickPoint = point;
            io.AddMousePosEvent(point.X, point.Y); RecordAction("right-aim", id); return false;
        }
        pendingClickId = null; io.AddMouseButtonEvent(1, true); RecordAction("right-press", id); return true;
    }
}
