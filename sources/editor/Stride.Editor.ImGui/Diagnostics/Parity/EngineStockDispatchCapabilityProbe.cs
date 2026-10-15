using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineStockDispatchCapabilityProbe : EngineUiCapabilityProbe
{
    internal EngineStockDispatchCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    private string path = "", control = "";
    private string[] locations = [];
    private int locationIndex;
    private readonly StockBrowserDispatchAttempt browserOpen = new();
    private bool reopenStarted;
    private int closePressFrame = -1, closeReleaseFrame = -1, closeAttempts;
    private bool closeNativeDown, closePopupObserved;
    private object? closePointerEvidence;
    private readonly StockFolderNavigationAttempt folderNavigation = new();
    private StockScriptDispatchState? baseline, edited, saved, reopened;
    private readonly List<object> checks = [];
    private const string EditedText = "namespace EditorParity { internal static class StockDispatchSource { } } // Ω 日本語 🚀";
    protected override string Scenario => "stock-dispatch.script";
    protected override double PhaseTimeoutSeconds => 45;
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new
    {
        path, control, locations, locationIndex, checks, browserAttempt = browserOpen.Evidence,
        closeGesture = new { closePressFrame, closeReleaseFrame, closeAttempts, closeNativeDown, closePopupObserved }, closePointerEvidence,
        nativeContext = Game.InspectStockScriptContext(),
        browserDispatch = path.Length == 0 ? null : Game.InspectStockBrowserDispatch(path),
        folderNavigation = folderNavigation.Evidence, folderPointer = Game.InspectStockFolderPointer(),
        input = path.Length == 0 ? null : Game.InspectStockScriptInput(),
        browser = path.Length == 0 ? null : Game.InspectStockDispatchBrowserState(path),
        coverage = "Actual browser default ScriptSourceFile dispatch, focused native Unicode source replacement, toolbar Undo/Redo/Save, independent file read, actual tab close and browser reopen. Fixture file writing is arrangement only. No cursor or clipboard."
    };
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                if (path.Length == 0) (path, control, locations) = Game.InspectStockScriptBrowserNavigation();
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
            case 2: SelectAll(false); foreach (var rune in Path.GetFileNameWithoutExtension(path).EnumerateRunes()) io.AddInputCharacter((uint)rune.Value); Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 5: browserOpen.Begin(Clock.Elapsed.TotalSeconds); if (Click(control)) Next(); break;
            case 6: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 7:
                baseline = Game.InspectStockScriptDispatchState();
                if (baseline == null || !Game.VerifyRecoveryAcceptanceNativeTab(path, out _)) break;
                Require(!baseline.Dirty, "Actual browser source dispatch must begin from its native clean saved baseline");
                if (Click("document-tab/" + baseline.Entry.Id)) Next(); break;
            case 8:
                var input = Game.InspectStockScriptInput();
                if (input?.Center is not { } point) break;
                Require(Game.Controls.Viewports.TryGetValue("document-tab/" + input.Value.EntryId, out var viewport),
                    "Actual source body typing requires its recorded native document viewport");
                io.AddMouseViewportEvent(viewport); io.AddMousePosEvent(point.X, point.Y);
                if (System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2) break;
                io.AddMouseButtonEvent(0, true); Next(); break;
            case 9: io.AddMouseButtonEvent(0, false); Next(); break;
            case 10:
                if (Game.InspectStockScriptInput()?.InputActive != true) break;
                Require(Game.VerifyRecoveryAcceptanceNativeFocus(path, out _), "Unicode editing must target the actual focused native source pane");
                SelectAll(true); Next(); break;
            case 11: SelectAll(false); Next(); break;
            case 12: foreach (var rune in EditedText.EnumerateRunes()) io.AddInputCharacter((uint)rune.Value); Next(); break;
            case 13:
                edited = Game.InspectStockScriptDispatchState();
                if (edited?.Text != EditedText) break;
                Require(edited.Dirty && ReferenceEquals(edited.Document, baseline!.Document) && ReferenceEquals(edited.State, baseline.State)
                    && edited.AssetId == baseline.AssetId && edited.DiskSha256 == baseline.DiskSha256,
                    "Actual Unicode input must change only its existing canonical source buffer, retaining native identity and original disk bytes");
                if (Click("toolbar/undo")) Next(); break;
            case 14:
                var undone = Game.InspectStockScriptDispatchState()!;
                if (undone.Text != baseline!.Text) break;
                Require(!undone.Dirty && undone.Document.CanRedo && undone.DiskSha256 == baseline.DiskSha256,
                    "Native source Undo must restore original text/savepoint without touching disk");
                if (Click("toolbar/redo")) Next(); break;
            case 15:
                var redone = Game.InspectStockScriptDispatchState()!;
                if (redone.Text != EditedText) break;
                Require(redone.Dirty && redone.Document.CanUndo && ReferenceEquals(redone.Document, baseline!.Document),
                    "Native source Redo must restore exact Unicode through the retained canonical owner");
                if (Click("toolbar/save")) Next(); break;
            case 16:
                saved = Game.InspectStockScriptDispatchState()!;
                if (saved.Dirty) break;
                Require(saved.Text == EditedText && File.ReadAllText(path, new UTF8Encoding(false, true)) == EditedText
                    && saved.AssetId == baseline!.AssetId && ReferenceEquals(saved.Document, baseline.Document),
                    "Actual Save must persist independently decoded exact Unicode with native identity and canonical owner unchanged");
                checks.Add(new { contract = "browser-edit-history-save", saved.AssetId, generation = saved.State.Generation,
                    saved.DiskSha256, originalDiskSha256 = baseline.DiskSha256, originalText = baseline.Text, saved.Text });
                if (!Game.Controls.Centers.TryGetValue("document-tab/" + saved.Entry.Id, out var tab) || !Aim("document-tab/" + saved.Entry.Id, tab)) break;
                var tabControl = "document-tab/" + saved.Entry.Id;
                var context = Gui.GetCurrentContext();
                Game.Controls.NativeIDs.TryGetValue(tabControl, out var expectedNativeId);
                closePointerEvidence = new { frame = Gui.GetFrameCount(), expectedNativeId,
                    recordedFrame = Game.Controls.NativeFrames.GetValueOrDefault(tabControl),
                    expectedViewport = Game.Controls.Viewports.GetValueOrDefault(tabControl), pointerViewport = io.MouseHoveredViewport,
                    x = io.MousePos.X, y = io.MousePos.Y, expectedX = tab.X, expectedY = tab.Y,
                    hoveredId = context.HoveredId, activeId = context.ActiveId,
                    hoveredWindow = context.HoveredWindow.IsNull ? 0u : context.HoveredWindow.ID,
                    navWindow = context.NavWindow.IsNull ? 0u : context.NavWindow.ID,
                    anyPopup = Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) };
                if (expectedNativeId == 0 || context.HoveredId != expectedNativeId) break;
                Require(Game.CanAcceptStockScriptContextClose(saved) && Game.VerifyRecoveryAcceptanceNativeTab(path, out _),
                    "Script context close requires the current clean canonical native selected owner");
                Require(++closeAttempts <= 3, "Script native context popup did not open after three acknowledged right-click attempts");
                closePressFrame = Gui.GetFrameCount(); closeNativeDown = false;
                io.AddMouseButtonEvent(1, true); Next(); break;
            case 17:
                if (Gui.GetFrameCount() <= closePressFrame) break;
                if (!Gui.IsMouseDown(ImGuiMouseButton.Right))
                {
                    Require(Gui.GetFrameCount() <= closePressFrame + 8, "Script right-button press was not processed"); break;
                }
                closeNativeDown = true; closeReleaseFrame = Gui.GetFrameCount();
                io.AddMouseButtonEvent(1, false); Next(); break;
            case 18:
                if (Gui.GetFrameCount() <= closeReleaseFrame + 1) break;
                if (Gui.IsMouseDown(ImGuiMouseButton.Right))
                {
                    Require(Gui.GetFrameCount() <= closeReleaseFrame + 8, "Script right-button release was not processed"); break;
                }
                var closeControl = "document-close/" + saved!.Entry.Id + "/close";
                if (!Game.Controls.Centers.ContainsKey(closeControl))
                {
                    if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) break;
                    if (Gui.GetFrameCount() > closeReleaseFrame + 8) SeekPhase(16);
                    break;
                }
                Require(closeNativeDown && Game.CanAcceptStockScriptContextClose(saved) && Game.VerifyRecoveryAcceptanceNativeTab(path, out _),
                    "Script close requires acknowledged native gesture and exact owned context control");
                closePopupObserved = true;
                if (Click(closeControl)) Next(); break;
            case 19:
                if (!Game.StockScriptDispatchClosed) break;
                Require(File.ReadAllText(path, new UTF8Encoding(false, true)) == EditedText,
                    "Actual clean tab close must retain saved Unicode source bytes");
                if (!reopenStarted) { browserOpen.Reset(); reopenStarted = true; }
                browserOpen.Begin(Clock.Elapsed.TotalSeconds);
                if (Click(control)) SeekPhase(30); break;
            case 30: if (browserOpen.Open(Game, path, control, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 31:
                reopened = Game.InspectStockScriptDispatchState();
                if (reopened == null || !Game.VerifyRecoveryAcceptanceNativeTab(path, out var pane)) break;
                Require(!reopened.Dirty && reopened.Text == EditedText && reopened.AssetId == saved!.AssetId
                    && reopened.ProjectPath == saved.ProjectPath && reopened.DiskSha256 == saved.DiskSha256,
                    "Default browser reopen must resolve the same native asset/project and exact saved source with one canonical owner");
                checks.Add(new { contract = "actual-close-reopen", reopened.AssetId, entryId = reopened.Entry.Id,
                    generation = reopened.State.Generation, retainedSameDocument = ReferenceEquals(reopened.Document, saved.Document), pane });
                Finish(true, "Stock ScriptSourceFile default dispatch, Unicode edit, native Undo/Redo/Save and actual clean close/reopen passed");
                break;
        }
    }
    private void SelectAll(bool down)
    { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
}

internal sealed partial class EditorGame
{
    internal bool CanAcceptStockScriptContextClose(StockScriptDispatchState expected)
        => CanPumpDocumentCloseRequests && !expected.Document.IsDirty
            && ReferenceEquals(documents.Find(expected.Entry.Path), expected.Entry)
            && ReferenceEquals(expected.Entry.Document, expected.Document)
            && ReferenceEquals(expected.Entry.State, expected.State)
            && IsCurrentDocumentPresentation(expected.Entry);
}
