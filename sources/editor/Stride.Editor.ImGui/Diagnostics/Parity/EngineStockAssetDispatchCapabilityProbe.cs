using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineStockAssetDispatchCapabilityProbe(EditorGame game, string artifacts, bool library)
    : EngineUiCapabilityProbe(game, artifacts)
{
    private string path = "", control = "";
    private string[] locations = [];
    private int locationIndex;
    private readonly StockBrowserDispatchAttempt browserOpen = new();
    private bool reopenStarted;
    private int undoFrame, redoFrame;
    private int undoAttempts, redoAttempts;
    private int closePressFrame = -1, closeReleaseFrame = -1, closeAttempts;
    private bool closeNativeDown, closePopupObserved;
    private object? closePointerEvidence;
    private readonly StockFolderNavigationAttempt folderNavigation = new();
    private StockAssetDispatchState? baseline, edited, saved, reopened;
    private readonly List<object> checks = [];
    private string Expected => library ? "Stock reusable – 星" : "2.5";
    protected override string Scenario => library ? "stock-dispatch.uilibrary" : "stock-dispatch.prefab";
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new { path, control, locations, locationIndex, library, checks,
        browser = path.Length == 0 ? null : Game.InspectStockDispatchBrowserState(path),
        folderNavigation = folderNavigation.Evidence, folderPointer = Game.InspectStockFolderPointer(),
        prefabInspector = library ? null : Game.InspectStockPrefabInspector(),
        historyReplay = path.Length == 0 ? null : Game.InspectStockHistoryReplay(library),
        browserDispatch = path.Length == 0 ? null : Game.InspectStockBrowserDispatch(path), browserAttempt = browserOpen.Evidence,
        closeGesture = new { closePressFrame, closeReleaseFrame, closeAttempts, closeNativeDown, closePopupObserved },
        closePointerEvidence,
        coverage = "Actual stock browser default dispatch, native standalone prefab transform or UILibrary root rename/export, toolbar Undo/Redo/Save, independent native asset deserialize, actual clean tab close/browser reopen. Fixture serializer construction is arrangement only; no physical input." };
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                if (path.Length == 0) (path, control, locations) = Game.InspectStockAssetBrowserNavigation(library);
                if (Click("tooltab/assets.folders")) SeekPhase(20); break;
            case 20:
                if (locationIndex == locations.Length) { if (Click("tooltab/assets.browser")) SeekPhase(25); break; }
                if (folderNavigation.Select(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 21:
                if (!folderNavigation.Select(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds)) break;
                if (locationIndex + 1 == locations.Length || Game.Controls.Expanded.GetValueOrDefault("location/" + locations[locationIndex]))
                { locationIndex++; SeekPhase(20); break; }
                if (folderNavigation.Expand(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds))
                { locationIndex++; SeekPhase(20); }
                break;
            case 22: io.AddKeyEvent(ImGuiKey.RightArrow, false); SeekPhase(20); break;
            case 25:
                if (!Game.StockDispatchFiltersEmpty) { Click("asset/filter/clear"); break; }
                if (Click("asset/filter")) SeekPhase(1); break;
            case 1: SelectAll(true); Next(); break;
            case 2: SelectAll(false); Type(Path.GetFileNameWithoutExtension(path)); Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 5:
                browserOpen.Begin(Clock.Elapsed.TotalSeconds);
                if (Click(control)) Next(); break;
            case 6: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 7:
                baseline = Game.InspectStockAssetDispatch(library);
                if (baseline == null || !Game.VerifyRecoveryAcceptanceNativeTab(path, out _)) break;
                Require(!baseline.Dirty && baseline.Value != Expected, "Stock dispatch must open actual clean distinguishing native fixture data");
                if (Click("document-tab/" + baseline.Entry.Id)) Next(); break;
            case 8:
                if (!library) { if (Click("entity/StockPrefabRoot")) SeekPhase(12); break; }
                if (!Game.Controls.Centers.ContainsKey("ui/element/" + baseline!.PartId)) { Click("ui/expand"); break; }
                if (Click("ui/element/" + baseline!.PartId)) Next(); break;
            case 9:
                if (!Game.Controls.Centers.TryGetValue("ui/element/" + baseline!.PartId, out var element)
                    || !Aim("ui/element/" + baseline.PartId, element)) break;
                io.AddMouseButtonEvent(1, true); Next(); break;
            case 10: io.AddMouseButtonEvent(1, false); Next(); break;
            case 11: if (Click("ui/menu/rename")) Next(); break;
            case 12:
                if (!library)
                {
                    if (!Game.StockPrefabRootSelected) { Click("entity/StockPrefabRoot"); break; }
                    if (!Game.Controls.Centers.ContainsKey("Position/0")) { Click("tooltab/editor.properties"); break; }
                }
                if (Click(library ? "ui/rename/" + baseline!.PartId : "Position/0")) SeekPhase(30); break;
            case 30: SelectAll(true); Next(); break;
            case 31: SelectAll(false); Next(); break;
            case 32: Type(Expected); Next(); break;
            case 33: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 34: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 35:
                // Numeric gestures commit on real field deactivation. UI rename
                // already commits on Enter; use its actual element row likewise.
                if (Click(library ? "ui/element/" + baseline!.PartId : "entity/StockPrefabRoot")) Next(); break;
            case 36:
                edited = Game.InspectStockAssetDispatch(library)!;
                if (edited.Value != Expected || !edited.Dirty) break;
                Require(ReferenceEquals(edited.Document, baseline!.Document) && ReferenceEquals(edited.State, baseline.State)
                    && edited.AssetId == baseline.AssetId && edited.PartId == baseline.PartId
                    && edited.HistoryPosition == baseline.HistoryPosition + 1 && edited.DiskSha256 == baseline.DiskSha256,
                    "Actual stock widget mutation must create one canonical native journal, retaining part/asset identity and untouched original bytes");
                if (!Game.StockHistoryToolbarReady("undo", path)) break;
                if (undoFrame == 0) undoFrame = Gui.GetFrameCount();
                if (Click("toolbar/undo"))
                {
                    Require(++undoAttempts <= 2, "Stock Undo exceeded two certified native gesture attempts");
                    Game.BeginStockHistoryToolbarGesture("undo", library); Next();
                }
                break;
            case 37:
                if (!Game.StockHistoryToolbarAccepted("undo", path, undoFrame))
                {
                    if (undoAttempts < 2 && Game.CertifyStockHistoryToolbarCancellation("undo", library))
                    { undoFrame = 0; SeekPhase(36); }
                    break;
                }
                var undone = Game.InspectStockAssetDispatch(library)!;
                if (undone.Value != baseline!.Value) break;
                Require(!undone.Dirty && undone.CanRedo && undone.HistoryPosition == baseline.HistoryPosition,
                    "Actual stock Undo must restore original native authored value/export/savepoint");
                Require(Game.StockHistoryToolbarAccepted("undo", path, undoFrame),
                    "Stock Undo requires genuine native toolbar activation for this current document");
                if (!Game.StockHistoryToolbarReady("redo", path)) break;
                if (redoFrame == 0) redoFrame = Gui.GetFrameCount();
                if (Click("toolbar/redo"))
                {
                    Require(++redoAttempts <= 2, "Stock Redo exceeded two certified native gesture attempts");
                    Game.BeginStockHistoryToolbarGesture("redo", library); Next();
                }
                break;
            case 38:
                if (!Game.StockHistoryToolbarAccepted("redo", path, redoFrame))
                {
                    if (redoAttempts < 2 && Game.CertifyStockHistoryToolbarCancellation("redo", library))
                    { redoFrame = 0; SeekPhase(37); }
                    break;
                }
                var redone = Game.InspectStockAssetDispatch(library)!;
                if (redone.Value != Expected) break;
                Require(redone.Dirty && redone.HistoryPosition == edited!.HistoryPosition && redone.CanUndo,
                    "Actual stock Redo must replay original journal without duplicated native operations");
                Require(Game.StockHistoryToolbarAccepted("redo", path, redoFrame),
                    "Stock Redo requires genuine native toolbar activation for this current document");
                if (Click("toolbar/save")) Next(); break;
            case 39:
                saved = Game.InspectStockAssetDispatch(library)!;
                if (saved.Dirty) break;
                var disk = Game.InspectStockAssetDispatch(library, independentDisk: true)!;
                Require(disk.Value == Expected && disk.AssetId == baseline!.AssetId && disk.PartId == baseline.PartId,
                    "Actual Save must independently roundtrip exact native stock type, asset/part IDs and authored value/export");
                checks.Add(new { contract = "default-dispatch-native-edit-undo-redo-save", disk.AssetId, disk.PartId,
                    disk.Value, disk.DiskSha256, generation = disk.State.Generation, history = disk.HistoryPosition });
                if (!Game.Controls.Centers.TryGetValue("document-tab/" + saved.Entry.Id, out var tab)
                    || !Aim("document-tab/" + saved.Entry.Id, tab)) break;
                var tabControl = "document-tab/" + saved.Entry.Id;
                var context = Gui.GetCurrentContext();
                Game.Controls.NativeIDs.TryGetValue(tabControl, out var expectedNativeId);
                closePointerEvidence = new { frame = Gui.GetFrameCount(), expectedNativeId,
                    recordedFrame = Game.Controls.NativeFrames.GetValueOrDefault(tabControl),
                    expectedViewport = Game.Controls.Viewports.GetValueOrDefault(tabControl),
                    pointerViewport = io.MouseHoveredViewport, x = io.MousePos.X, y = io.MousePos.Y,
                    expectedX = tab.X, expectedY = tab.Y, hoveredId = context.HoveredId,
                    hoveredWindow = context.HoveredWindow.IsNull ? 0u : context.HoveredWindow.ID,
                    hoveredUnderMoving = context.HoveredWindowUnderMovingWindow.IsNull ? 0u : context.HoveredWindowUnderMovingWindow.ID,
                    navWindow = context.NavWindow.IsNull ? 0u : context.NavWindow.ID,
                    activeId = context.ActiveId, anyPopup = Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) };
                if (expectedNativeId == 0 || context.HoveredId != expectedNativeId) break;
                Require(Game.VerifyRecoveryAcceptanceNativeTab(path, out _) && ReferenceEquals(saved.Document, baseline.Document),
                    "Context close must target the selected canonical stock document");
                Require(++closeAttempts <= 3, "Native document context popup did not open after three acknowledged right-click attempts");
                closePressFrame = Gui.GetFrameCount(); closeNativeDown = false;
                io.AddMouseButtonEvent(1, true); Next(); break;
            case 40:
                if (Gui.GetFrameCount() <= closePressFrame) break;
                if (!Gui.IsMouseDown(ImGuiMouseButton.Right))
                {
                    Require(Gui.GetFrameCount() <= closePressFrame + 8, "Native document right-button press was not processed");
                    break;
                }
                closeNativeDown = true; closeReleaseFrame = Gui.GetFrameCount();
                io.AddMouseButtonEvent(1, false); Next(); break;
            case 41:
                if (Gui.GetFrameCount() <= closeReleaseFrame + 1) break;
                if (Gui.IsMouseDown(ImGuiMouseButton.Right))
                {
                    Require(Gui.GetFrameCount() <= closeReleaseFrame + 8, "Native document right-button release was not processed");
                    break;
                }
                var closeControl = "document-close/" + saved!.Entry.Id + "/close";
                if (!Game.Controls.Centers.ContainsKey(closeControl))
                {
                    if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) break;
                    if (Gui.GetFrameCount() > closeReleaseFrame + 8) SeekPhase(39);
                    break;
                }
                Require(closeNativeDown && Game.CanAcceptStockContextClose(saved)
                    && Game.VerifyRecoveryAcceptanceNativeTab(path, out _),
                    "Native close popup requires acknowledged right-button gesture and current selected stock owner");
                closePopupObserved = true;
                if (Click(closeControl)) Next(); break;
            case 42:
                if (!Game.StockAssetDispatchClosed(library)) break;
                if (!reopenStarted) { browserOpen.Reset(); reopenStarted = true; }
                browserOpen.Begin(Clock.Elapsed.TotalSeconds);
                if (Click(control)) Next(); break;
            case 43: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 44:
                reopened = Game.InspectStockAssetDispatch(library, independentDisk: true);
                if (reopened == null || !Game.VerifyRecoveryAcceptanceNativeTab(path, out var pane)) break;
                Require(!reopened.Dirty && reopened.Value == Expected && reopened.AssetId == saved!.AssetId
                    && reopened.PartId == saved.PartId && reopened.DiskSha256 == saved.DiskSha256,
                    "Actual stock clean close/default browser reopen must retain native serialized identity/value and a unique current canonical owner");
                checks.Add(new { contract = "native-close-reopen", reopened.AssetId, reopened.PartId, reopened.Value,
                    generation = reopened.State.Generation, retainedSameDocument = ReferenceEquals(reopened.Document, saved.Document), pane });
                Finish(true, library ? "Native UILibrary dispatch/root export rename/history/save/reopen passed" : "Native Prefab dispatch/transform/history/save/reopen passed");
                break;
        }
    }
    private static void SelectAll(bool down) { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
    private static void Type(string value) { foreach (var rune in value.EnumerateRunes()) Gui.GetIO().AddInputCharacter((uint)rune.Value); }
}

internal sealed partial class EditorGame
{
    internal bool CanAcceptStockContextClose(StockAssetDispatchState expected)
        => CanPumpDocumentCloseRequests && !expected.Document.IsDirty
            && ReferenceEquals(documents.Find(expected.Entry.Path), expected.Entry)
            && ReferenceEquals(expected.Entry.Document, expected.Document)
            && ReferenceEquals(expected.Entry.State, expected.State)
            && IsCurrentDocumentPresentation(expected.Entry);
}
