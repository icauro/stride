using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineUiCanvasCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private readonly StockFolderNavigationAttempt folders = new();
    private readonly StockBrowserDispatchAttempt browser = new();
    private readonly List<object> checks = [];
    private string path = "", tile = "";
    private string[] locations = [];
    private int locationIndex, gesture, baselinePosition, gesturePosition, pressFrame, toolbarFrame;
    private UiCanvasReceipt? original, before, after;
    private UiCanvasContext? context;
    private object? initialIsolation;
    private Vector2 pointer;
    protected override string Scenario => "ui.canvas-ownership";
    internal object CapabilityEvidence => Evidence;
    protected override object Evidence => new { path, gesture, baselinePosition, gesturePosition, pressFrame, checks,
        initialIsolation, contextVerification = Game.UiCanvasContextVerificationEvidence, current = Game.InspectUiCanvasParityEvidence(), folder = folders.Evidence, browser = browser.Evidence,
        coverage = "Actual browser Edit dispatch and native compiled UI canvas pointer pick/move/resize/Escape; native toolbar history/save and independent authored YAML. Fixture native serialization is arrangement, not UI creation. Outer terminal required." };
    private void Point(UiCanvasReceipt receipt, Vector2 point)
    {
        var io = Gui.GetIO(); io.AddMouseViewportEvent(receipt.ViewportId); io.AddMousePosEvent(point.X, point.Y);
    }
    protected override void Step()
    {
        var io = Gui.GetIO(); var current = Game.InspectUiCanvasParity();
        switch (Phase)
        {
            case 0:
                if (!Game.DocumentGroupParityCaseQuiescent) break;
                (path, tile, locations) = Game.InspectUiCanvasBrowserNavigation();
                if (Click("tooltab/assets.folders")) Next(); break;
            case 1:
                if (locationIndex == locations.Length) { if (Click("tooltab/assets.browser")) SeekPhase(3); break; }
                if (!folders.Select(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds)) break;
                if (locationIndex + 1 < locations.Length && !Game.Controls.Expanded.GetValueOrDefault("location/" + locations[locationIndex]))
                { folders.Expand(Game, locations[locationIndex], Click, Clock.Elapsed.TotalSeconds); break; }
                locationIndex++; break;
            case 3:
                if (!Game.StockDispatchFiltersEmpty) { Click("asset/filter/clear"); break; }
                if (Click("asset/filter")) Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 5:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in Path.GetFileNameWithoutExtension(path)) io.AddInputCharacter(character);
                Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 7: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 8: if (browser.Open(Game, path, tile, Click, Clock.Elapsed.TotalSeconds)) Next(); break;
            case 9:
                if (current is not { NativeTargetReady: true } || !SamePath(current.Document.Path, path) || current.Authored != current.Runtime) break;
                Require(current.BodyVisible, "Initial native canvas picking requires its actual visible projected body.");
                original = current; baselinePosition = Game.UiCanvasParityHistoryPosition;
                context = Game.CaptureUiCanvasParityContext(current);
                initialIsolation = Game.CaptureUiCanvasInitialIsolationEvidence(context);
                Require(context.OtherDirty.All(pair => !pair.Value),
                    "UI canvas Save All isolation requires unrelated authored documents initially clean: "
                    + System.Text.Json.JsonSerializer.Serialize(initialIsolation));
                Point(current, current.Body); Next(); break;
            case 10:
                if (current == null || Vector2.DistanceSquared(io.MousePos, current.Body) > 2) { if (current != null) Point(current, current.Body); break; }
                io.AddMouseButtonEvent(0, true); pressFrame = Gui.GetFrameCount(); Next(); break;
            case 11:
                if (current is not { PointerCaptured: true } || !Gui.IsMouseDown(ImGuiMouseButton.Left)) break;
                io.AddMouseButtonEvent(0, false); Next(); break;
            case 12:
                if (current is not { Selected: true, PointerCaptured: false } || Gui.IsMouseDown(ImGuiMouseButton.Left)) break;
                Require(Game.UiCanvasParityHistoryPosition == baselinePosition && current.Authored == original!.Authored,
                    "Native canvas release selection must preserve authored layout and journal.");
                checks.Add(new { contract = "native-canvas-release-pick", current.ElementId, current.ItemId, current.ViewportId });
                SeekPhase(20); break;
            case 20:
                if (current is not { Selected: true, NativeTargetReady: true } || current.Authored != current.Runtime) break;
                Require(gesture == 1 ? current.ResizeVisible : current.BodyVisible,
                    "Native gesture start requires the actual body or sizing handle to be inside the current canvas.");
                before = current; gesturePosition = Game.UiCanvasParityHistoryPosition;
                pointer = gesture == 1 ? current.Resize : current.Body; Point(current, pointer); Next(); break;
            case 21:
                if (current == null || Vector2.DistanceSquared(io.MousePos, pointer) > 2) break;
                io.AddMouseButtonEvent(0, true); pressFrame = Gui.GetFrameCount(); Next(); break;
            case 22:
                if (current is not { PointerCaptured: true } || !Gui.IsMouseDown(ImGuiMouseButton.Left)) break;
                Require(current.MovingElement == current.ElementId && current.Resizing == (gesture == 1),
                    "Native pointer press must acquire the actual selected move body or sizing handle, not a different gesture.");
                Point(current, pointer + new Vector2(35, 23)); Next(); break;
            case 23:
                if (current is not { RuntimeChanged: true } || current.Runtime == before!.Runtime) break;
                Require(current.Authored == before.Authored && Game.UiCanvasParityHistoryPosition == gesturePosition,
                    "Native held gesture must mutate only the engine clone before release.");
                if (gesture == 2) io.AddKeyEvent(ImGuiKey.Escape, true);
                else io.AddMouseButtonEvent(0, false);
                Next(); break;
            case 24:
                if (gesture == 2) { io.AddKeyEvent(ImGuiKey.Escape, false); io.AddMouseButtonEvent(0, false); Next(); break; }
                if (current is not { PointerCaptured: false } || Gui.IsMouseDown(ImGuiMouseButton.Left) || current.Authored != current.Runtime) break;
                Require(current.Authored != before!.Authored && Game.UiCanvasParityHistoryPosition == gesturePosition + 1,
                    "Accepted native move/resize must publish exactly one actual layout transaction.");
                after = current; toolbarFrame = Gui.GetFrameCount(); SeekPhase(30); break;
            case 25:
                if (current is not { PointerCaptured: false } || Gui.IsMouseDown(ImGuiMouseButton.Left) || current.Runtime != before!.Runtime) break;
                Require(current.Authored == before.Authored && Game.UiCanvasParityHistoryPosition == gesturePosition,
                    "Escape must preserve authored layout/history and resynchronize the real engine clone.");
                checks.Add(new { contract = "native-Escape-no-mutation", current.ElementId });
                toolbarFrame = Gui.GetFrameCount(); SeekPhase(40); break;
            case 30: if (!Game.StockHistoryToolbarAccepted("undo", path, toolbarFrame)) { Click("toolbar/undo"); break; } Next(); break;
            case 31:
                if (current == null || current.Runtime != before!.Runtime) break;
                Require(current.Authored == before.Authored && Game.UiCanvasParityHistoryPosition == gesturePosition, "Actual toolbar Undo must restore native layout.");
                toolbarFrame = Gui.GetFrameCount(); Next(); break;
            case 32: if (!Game.StockHistoryToolbarAccepted("redo", path, toolbarFrame)) { Click("toolbar/redo"); break; } Next(); break;
            case 33:
                if (current == null || current.Runtime != after!.Runtime) break;
                Require(current.Authored == after.Authored && Game.UiCanvasParityHistoryPosition == gesturePosition + 1, "Actual toolbar Redo must restore native layout.");
                checks.Add(new { contract = gesture == 0 ? "native-move-one-transaction" : "native-resize-one-transaction", current.ElementId, before = before!.Authored, after = after.Authored });
                gesture++; SeekPhase(20); break;
            case 40: if (Click("toolbar/save")) Next(); break;
            case 41:
                if (current == null || current.Document.IsDirty || !Game.DocumentGroupParityCaseQuiescent) break;
                Game.VerifyUiCanvasPersisted(current); checks.Add(new { contract = "independent-native-YAML-layout-and-alias" });
                toolbarFrame = Gui.GetFrameCount(); Next(); break;
            case 42: if (!Game.StockHistoryToolbarAccepted("undo", path, toolbarFrame)) { Click("toolbar/undo"); break; } Next(); break;
            case 43:
                if (Game.UiCanvasParityHistoryPosition != baselinePosition + 1) break;
                toolbarFrame = Gui.GetFrameCount(); Next(); break;
            case 44: if (!Game.StockHistoryToolbarAccepted("undo", path, toolbarFrame)) { Click("toolbar/undo"); break; } Next(); break;
            case 45:
                if (current == null || current.Runtime != original!.Runtime || Game.UiCanvasParityHistoryPosition != baselinePosition) break;
                Require(current.Authored == original.Authored, "Owned two-transaction cleanup must restore original authored layout.");
                if (Click("toolbar/save")) Next(); break;
            case 46:
                if (current == null || current.Document.IsDirty || !Game.DocumentGroupParityCaseQuiescent) break;
                Game.VerifyUiCanvasPersisted(current);
                Require(current.Authored == original!.Authored && Game.UiCanvasParityHistoryPosition == baselinePosition, "Cleanup must leave original native layout and applied history.");
                Game.VerifyUiCanvasParityContext(context!, current);
                checks.Add(new { contract = "original-canonical-owner-history-participants-and-unrelated-files-preserved" });
                Finish(true, "Native UI canvas pick/move/resize/cancel and toolbar history/save verified; outer terminal required."); break;
        }
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    protected override void CleanInput() => Gui.GetIO().AddKeyEvent(ImGuiKey.Escape, false);
}
