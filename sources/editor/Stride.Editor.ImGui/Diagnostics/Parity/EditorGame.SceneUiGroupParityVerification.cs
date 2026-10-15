using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record SceneUiNativeOwner(UIEditorViewport View, object SceneSystem, object Page, Texture Target, float CameraSize, bool Visible);
internal sealed partial class UIEditorPanel
{
    internal SceneUiNativeOwner? InspectSceneUiNativeOwner() => viewport?.InspectSceneUiNativeOwner();
}
internal sealed partial class UIEditorViewport
{
    internal SceneUiNativeOwner? InspectSceneUiNativeOwner() => !disposed && RenderError == null && (presentation ?? target) is { IsDisposed: false } output
        && component.Page is { } page ? new(this, sceneSystem, page, output, camera.OrthographicSize, Visible) : null;
}
internal sealed partial class EditorGame
{
    private SceneUiGroupParityRun? sceneUiGroupParity;
    internal object? SceneUiGroupParityEvidence => sceneUiGroupParity?.Evidence;
    internal object? SceneUiGroupParityProgress => sceneUiGroupParity?.Inspection;
    internal bool StepSceneUiGroupParity(Func<string, bool> click)
    {
        if (!Controls.Enabled || !args.Contains("--ui-test")) throw new InvalidOperationException("Scene/UI groups require the isolated synthetic-input fixture.");
        sceneUiGroupParity ??= new(this);
        return sceneUiGroupParity.Step(click);
    }
    internal void CleanSceneUiGroupParityInput()
    {
        sceneUiGroupParity?.WritePassiveReceipt("cleanup");
        Gui.GetIO().AddMouseButtonEvent(0, false); Gui.GetIO().AddMouseButtonEvent(1, false);
    }

    private sealed class SceneUiGroupParityRun
    {
        private readonly EditorGame game;
        private readonly EditorDocumentEntry scene, ui;
        private readonly EditorAuthoringState sceneOwner, uiOwner;
        private readonly int historyPosition;
        private readonly Guid[] appliedIds;
        private readonly byte[] sceneBytes, uiBytes;
        private readonly int originalGroups;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int phase, rightFrame, releaseFrame;
        private bool returning;
        private DocumentGroupParityView? sceneBaseline;
        private SceneUiNativeOwner? uiBaseline;
        private EngineOutputSample? beforeZoom;
        private EngineOutputDifference? zoomOutput;
        private readonly List<object> receipts = [];
        private object Transactions() => game.sessionHistory!.Stack.RetrieveAllTransactions().Select(transaction => new
        {
            transaction.Id, Name = game.sessionHistory.History.Entries.Single(entry => entry.Id == transaction.Id).Name,
            participants = game.sessionHistory.GetParticipants(transaction).Select(owner => new
            {
                owner.CanonicalPath, documentType = owner.Document.GetType().FullName,
                assetId = owner.AssetId?.ToString(), owner.Generation, owner.IsPublished, owner.IsDisposed,
            }).ToArray(),
        }).ToArray();
        internal object Evidence => new { phase, elapsed = clock.Elapsed.TotalSeconds, sceneEntry = scene.Id, uiEntry = ui.Id,
            sceneGeneration = sceneOwner.Generation, uiGeneration = uiOwner.Generation, historyPosition, receipts,
            coverage = "Actual native tab context split/previous commands, simultaneous engine scene/UI owners, UI canvas pick and wheel zoom with real GPU readback; UI borrows host Game/device intentionally. No OS input, float/HWND, Scene picking or authored UI edits claim. Outer terminal required." };
        private static int Identity(object? value) => value == null ? 0 : RuntimeHelpers.GetHashCode(value);
        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private object BaselineGuards()
        {
            var transactions = game.sessionHistory!.Stack.RetrieveAllTransactions().ToArray();
            var currentApplied = transactions.Take(historyPosition).Select(transaction => transaction.Id).ToArray();
            object FileGuard(string path, byte[] expected)
            {
                var exists = File.Exists(path);
                var actual = exists ? File.ReadAllBytes(path) : [];
                return new { path, exists, expectedSha256 = Sha(expected), actualSha256 = exists ? Sha(actual) : null,
                    expectedLength = expected.Length, actualLength = actual.Length, bytesEqual = exists && actual.AsSpan().SequenceEqual(expected) };
            }
            return new
            {
                sceneStateSame = ReferenceEquals(scene.State, sceneOwner), uiStateSame = ReferenceEquals(ui.State, uiOwner),
                sceneOwnerPublished = sceneOwner.IsPublished, uiOwnerPublished = uiOwner.IsPublished,
                sceneOwnerDisposed = sceneOwner.IsDisposed, uiOwnerDisposed = uiOwner.IsDisposed,
                expectedSceneState = Identity(sceneOwner), actualSceneState = Identity(scene.State),
                expectedUiState = Identity(uiOwner), actualUiState = Identity(ui.State),
                expectedSceneGeneration = sceneOwner.Generation, actualSceneGeneration = scene.State?.Generation,
                expectedUiGeneration = uiOwner.Generation, actualUiGeneration = ui.State?.Generation,
                expectedHistoryPosition = historyPosition, actualHistoryPosition = game.sessionHistory.History.Position,
                historyPositionEqual = game.sessionHistory.History.Position == historyPosition,
                expectedAppliedTransactionIds = appliedIds, actualAppliedTransactionIds = currentApplied,
                appliedTransactionIdsEqual = currentApplied.SequenceEqual(appliedIds),
                allTransactionIds = transactions.Select(transaction => transaction.Id).ToArray(),
                transactions = Transactions(),
                sceneFile = FileGuard(scene.Path, sceneBytes), uiFile = FileGuard(ui.Path, uiBytes),
            };
        }
        internal object Inspection
        {
            get
            {
                var sceneView = game.InspectDocumentGroupParity(scene.Id); var uiView = game.InspectDocumentGroupParity(ui.Id);
                var native = UiNative(); var canvas = Canvas(); var io = Gui.GetIO(); var context = Gui.GetCurrentContext();
                object? Group(DocumentGroupParityView? view) => view == null ? null : new
                {
                    view.GroupId, view.GroupItems, view.GroupActiveId, view.DockId, view.RootDockId, view.ParentDockId,
                    view.ParentSplitAxis, view.MainRoot, view.NativeActive, view.Visible, view.RenderRevision,
                    position = new { view.Position.X, view.Position.Y }, size = new { view.Size.X, view.Size.Y },
                    view.ViewportId, view.NativeWindow, nativeTabs = view.NativeTabs,
                };
                return new
                {
                    phase, returning, elapsed = clock.Elapsed.TotalSeconds, actualFrame = Gui.GetFrameCount(), rightFrame, releaseFrame,
                    nativeRestorationSettled = game.NativeDocumentRestorationSettled, globalActive = game.documents.Active?.Id,
                    commandOwnerPath = game.CommandDocument?.Path, scene = Group(sceneView), ui = Group(uiView),
                    sceneGameIdentity = Identity(sceneView?.View?.RuntimeGame), sceneSystemIdentity = Identity(sceneView?.View?.RuntimeGame?.SceneSystem),
                    sceneRootIdentity = Identity(sceneView?.Runtime), sceneTargetIdentity = Identity(sceneView?.View?.Target),
                    uiViewIdentity = Identity(native?.View), uiSceneSystemIdentity = Identity(native?.SceneSystem), uiPageIdentity = Identity(native?.Page),
                    uiTargetIdentity = Identity(native?.Target), uiVisible = native?.Visible,
                    uiTarget = native == null ? null : new { native.Target.Width, native.Target.Height, format = native.Target.Format.ToString(), native.Target.IsDisposed },
                    uiCameraSize = native?.CameraSize, baselineUiCameraSize = uiBaseline?.CameraSize,
                    sceneCamera = sceneView?.Camera.ToString(), baselineSceneCamera = sceneBaseline?.Camera.ToString(),
                    mouse = new { io.MousePos.X, io.MousePos.Y }, wheel = io.MouseWheel, io.MouseHoveredViewport,
                    nativeLeftDown = Gui.IsMouseDown(ImGuiMouseButton.Left), nativeRightDown = Gui.IsMouseDown(ImGuiMouseButton.Right),
                    context.HoveredId, context.ActiveId, popups = context.OpenPopupStack.Size,
                    canvas = canvas == null ? null : new { canvas.Frame, canvas.ViewportId, canvas.ItemId,
                        canvas.BodyVisible, canvas.ResizeVisible, canvas.Selected, canvas.PointerCaptured, canvas.RuntimeChanged,
                        body = new { canvas.Body.X, canvas.Body.Y }, canvas.Authored, canvas.Runtime },
                    gpuOutputDifference = zoomOutput == null ? null : new { zoomOutput.Value.Pixels, zoomOutput.Value.ChangedPixels, zoomOutput.Value.AbsoluteRgbDelta },
                    baselineGuards = BaselineGuards(),
                    nativeGuards = new
                    {
                        dockIdsDistinct = sceneView != null && uiView != null && sceneView.DockId != uiView.DockId,
                        sameRoot = sceneView != null && uiView != null && sceneView.RootDockId == uiView.RootDockId,
                        sceneMainRoot = sceneView?.MainRoot, uiMainRoot = uiView?.MainRoot,
                        sceneGroupActiveMatches = sceneView?.GroupActiveId == scene.Id, uiGroupActiveMatches = uiView?.GroupActiveId == ui.Id,
                        targetPresent = sceneView?.View?.Target != null,
                        targetsDistinct = native != null && sceneView?.View?.Target != null && !ReferenceEquals(sceneView.View.Target, native.Target),
                        sceneSystemPresent = sceneView?.View?.RuntimeGame?.SceneSystem != null,
                        systemsDistinct = native != null && sceneView?.View?.RuntimeGame?.SceneSystem is { } actualSystem && !ReferenceEquals(actualSystem, native.SceneSystem),
                        activeUiEntrySame = ReferenceEquals(game.documents.Active, ui), uiCommandOwnerSame = ReferenceEquals(game.CommandDocument, ui.Document),
                        uiViewSame = native != null && uiBaseline != null && ReferenceEquals(native.View, uiBaseline.View),
                        uiTargetSame = native != null && uiBaseline != null && ReferenceEquals(native.Target, uiBaseline.Target),
                        sceneCameraEqual = sceneView != null && sceneBaseline != null && sceneView.Camera == sceneBaseline.Camera,
                        sceneRuntimeSame = sceneView != null && sceneBaseline != null && ReferenceEquals(sceneView.Runtime, sceneBaseline.Runtime),
                        baselineRenderRevision = sceneBaseline?.RenderRevision, actualRenderRevision = sceneView?.RenderRevision,
                        sceneRenderAdvanced = sceneView != null && sceneBaseline != null && sceneView.RenderRevision > sceneBaseline.RenderRevision,
                        sceneVisible = sceneView?.Visible,
                    },
                    receipts,
                    identityBoundary = "Object identity hashes identify retained instances only within this owned process; native document GUIDs/generations and HWND/dock/viewport IDs remain separate."
                };
            }
        }
        internal void WritePassiveReceipt(string reason)
        {
            var directory = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Scene/UI diagnostic evidence requires the owned artifact directory.");
            File.WriteAllText(Path.Combine(directory, "scene-ui-split-" + reason + ".json"), JsonSerializer.Serialize(Inspection));
            if (reason == "failure")
            {
                if (File.Exists(scene.Path)) File.WriteAllBytes(Path.Combine(directory, "scene-ui-split-actual-scene.bin"), File.ReadAllBytes(scene.Path));
                if (File.Exists(ui.Path)) File.WriteAllBytes(Path.Combine(directory, "scene-ui-split-actual-ui.bin"), File.ReadAllBytes(ui.Path));
            }
        }
        internal SceneUiGroupParityRun(EditorGame game)
        {
            this.game = game;
            scene = game.documents.Active ?? throw new InvalidOperationException("The original scene must be active before mixed grouping.");
            if (scene.Document is not EntityHierarchyDocument) throw new InvalidOperationException("Restore the real Scene tab before mixed grouping.");
            ui = game.documents.Find(game.UiCanvasParityFixturePath) ?? throw new InvalidOperationException("Run the actual native UI canvas fixture first; its retained view is required.");
            if (ui.Document is not UIDocument || game.DocumentGroupPanel.Model.Find(scene) != game.DocumentGroupPanel.Model.Find(ui))
                throw new InvalidOperationException("The real clean scene/UI entries must initially share one document group.");
            sceneOwner = scene.State ?? throw new InvalidOperationException("No scene canonical state.");
            uiOwner = ui.State ?? throw new InvalidOperationException("No UI canonical state.");
            var preferencesPositionBefore = game.sessionHistory!.History.Position;
            var preferencesIdsBefore = game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
            var preferencesTransactionsBefore = Transactions();
            var sceneBeforePreferences = File.ReadAllBytes(scene.Path);
            var uiBeforePreferences = File.ReadAllBytes(ui.Path);
            // Fixture preparation calls the same native persistence routine used by
            // the normal idle scene toolbar, before defining the no-edit baseline.
            // This remains service arrangement, not evidence of a toolbar click.
            game.SaveNativeSceneDisplaySettings();
            receipts.Add(new
            {
                contract = "native-scene-toolbar-preferences-baseline-arrangement",
                source = "SceneToolbarContext.DrawSceneToolbar -> SaveNativeSceneDisplaySettings -> PackageSettingsDocument.Set/Save",
                preferencesPositionBefore, preferencesPositionAfter = game.sessionHistory.History.Position,
                preferencesTransactionsBefore, preferencesTransactionsAfter = Transactions(),
                sceneShaBefore = Sha(sceneBeforePreferences), sceneShaAfter = Sha(File.ReadAllBytes(scene.Path)),
                uiShaBefore = Sha(uiBeforePreferences), uiShaAfter = Sha(File.ReadAllBytes(ui.Path)),
                coverage = "Native preference service fixture arrangement before strict interaction history baseline; no UI click claim.",
            });
            if (game.sessionHistory.History.Position != preferencesPositionBefore
                || !game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).SequenceEqual(preferencesIdsBefore)
                || !File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBeforePreferences)
                || !File.ReadAllBytes(ui.Path).AsSpan().SequenceEqual(uiBeforePreferences))
                throw new InvalidOperationException("Automatic native scene preference baseline arrangement must preserve the exact authored history and both authored asset files.");
            historyPosition = game.sessionHistory!.History.Position;
            appliedIds = game.sessionHistory.Stack.RetrieveAllTransactions().Take(historyPosition).Select(transaction => transaction.Id).ToArray();
            sceneBytes = File.ReadAllBytes(scene.Path); uiBytes = File.ReadAllBytes(ui.Path);
            originalGroups = game.DocumentGroupPanel.Model.Groups.Count();
            var directory = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("Scene/UI baseline requires the owned artifact directory.");
            File.WriteAllBytes(Path.Combine(directory, "scene-ui-split-baseline-scene.bin"), sceneBytes);
            File.WriteAllBytes(Path.Combine(directory, "scene-ui-split-baseline-ui.bin"), uiBytes);
            WritePassiveReceipt("baseline");
        }
        private void Require(bool condition, string message)
        {
            if (condition) return;
            WritePassiveReceipt("failure");
            throw new InvalidOperationException(message);
        }
        private SceneUiNativeOwner? UiNative() => game.ownedUIEditors.GetValueOrDefault((UIDocument)ui.Document)?.InspectSceneUiNativeOwner();
        private UiCanvasReceipt? Canvas() => game.ownedUIEditors.GetValueOrDefault((UIDocument)ui.Document)?.InspectCanvasParity(UiWorkflowTest.AuthoredUiButtonId);
        private void Pointer(UiCanvasReceipt canvas, System.Numerics.Vector2 point)
        { var io = Gui.GetIO(); io.AddMouseViewportEvent(canvas.ViewportId); io.AddMousePosEvent(point.X, point.Y); }
        internal bool Step(Func<string, bool> click)
        {
            Require(clock.Elapsed.TotalSeconds < 90, "Scene/UI split workflow exceeded its bounded native-input deadline.");
            var io = Gui.GetIO();
            switch (phase)
            {
                case 0:
                    if (!game.NativeDocumentRestorationSettled) return false;
                    if (!game.VerifyRecoveryAcceptanceNativeTab(ui.Path, out _)) { click("document-tab/" + ui.Id); return false; }
                    phase = 1; return false;
                case 1:
                    var tab = "document-tab/" + ui.Id;
                    if (!game.Controls.Centers.TryGetValue(tab, out var point) || !game.Controls.NativeIDs.TryGetValue(tab, out var nativeId)) return false;
                    io.AddMouseViewportEvent(game.Controls.Viewports.GetValueOrDefault(tab)); io.AddMousePosEvent(point.X, point.Y);
                    if (System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2 || Gui.GetCurrentContext().HoveredId != nativeId) return false;
                    rightFrame = Gui.GetFrameCount(); io.AddMouseButtonEvent(1, true); phase = 2; return false;
                case 2:
                    if (!Gui.IsMouseDown(ImGuiMouseButton.Right)) { Require(Gui.GetFrameCount() <= rightFrame + 8, "Native tab context press was not accepted."); return false; }
                    releaseFrame = Gui.GetFrameCount(); io.AddMouseButtonEvent(1, false); phase = 3; return false;
                case 3:
                    if (Gui.GetFrameCount() <= releaseFrame + 1 || Gui.IsMouseDown(ImGuiMouseButton.Right)) return false;
                    if (click("document-command/" + ui.Id + "/" + (returning ? EditorDocumentGroupCommand.Previous : EditorDocumentGroupCommand.Horizontal))) phase = returning ? 12 : 4;
                    return false;
                case 4:
                    var sceneView = game.InspectDocumentGroupParity(scene.Id); var uiView = game.InspectDocumentGroupParity(ui.Id); var native = UiNative();
                    if (!game.NativeDocumentRestorationSettled || sceneView is not { Visible: true, NativeActive: true, View: not null }
                        || uiView is not { NativeActive: true } || native is not { Visible: true } || sceneView.GroupId == uiView.GroupId) return false;
                    Require(sceneView.DockId != uiView.DockId && sceneView.RootDockId == uiView.RootDockId && sceneView.MainRoot && uiView.MainRoot
                        && sceneView.GroupActiveId == scene.Id && uiView.GroupActiveId == ui.Id
                        && sceneView.View.Target != null && !ReferenceEquals(sceneView.View.Target, native.Target)
                        && sceneView.View.RuntimeGame?.SceneSystem is { } sceneSystem
                        && !ReferenceEquals(sceneSystem, native.SceneSystem), "Split must retain independent actual native pane/scene-system/target owners under the real main root.");
                    sceneBaseline = sceneView; uiBaseline = native;
                    Require(Math.Abs(native.CameraSize - Stride.Engine.CameraComponent.DefaultOrthographicSize) <= 0.00001f,
                        "The isolated UI fixture must start at the actual native default orthographic size before Reset Zoom cleanup.");
                    beforeZoom = EngineOutputSample.Read(game.GraphicsContext.CommandList, native.Target);
                    receipts.Add(new { contract = "two-visible-native-scene-ui-panes", sceneDock = sceneView.DockId, uiDock = uiView.DockId,
                        root = sceneView.RootDockId, sceneTarget = new { sceneView.View.Target.Width, sceneView.View.Target.Height }, uiTarget = new { native.Target.Width, native.Target.Height } });
                    phase = 5; return false;
                case 5:
                    if (Canvas() is not { BodyVisible: true } canvas) return false;
                    Pointer(canvas, canvas.Body);
                    if (System.Numerics.Vector2.DistanceSquared(io.MousePos, canvas.Body) > 2) return false;
                    io.AddMouseButtonEvent(0, true); phase = 6; return false;
                case 6:
                    if (Canvas() is not { PointerCaptured: true } || !Gui.IsMouseDown(ImGuiMouseButton.Left)) return false;
                    io.AddMouseButtonEvent(0, false); phase = 7; return false;
                case 7:
                    if (Canvas() is not { Selected: true, PointerCaptured: false, BodyVisible: true } selected || Gui.IsMouseDown(ImGuiMouseButton.Left)) return false;
                    Require(ReferenceEquals(game.documents.Active, ui) && ReferenceEquals(game.CommandDocument, ui.Document), "Actual UI canvas selection must route the current native command/Inspector owner to the canonical UI document.");
                    Pointer(selected, selected.Body); io.AddMouseWheelEvent(0, 1); phase = 8; return false;
                case 8:
                    native = UiNative(); sceneView = game.InspectDocumentGroupParity(scene.Id);
                    if (native == null || sceneView == null || native.CameraSize == uiBaseline!.CameraSize) return false;
                    Require(ReferenceEquals(native.View, uiBaseline.View) && ReferenceEquals(native.Target, uiBaseline.Target)
                        && sceneView.Camera == sceneBaseline!.Camera && ReferenceEquals(sceneView.Runtime, sceneBaseline.Runtime)
                        && sceneView.RenderRevision > sceneBaseline.RenderRevision && sceneView.Visible,
                        "Native UI zoom must preserve the independent scene camera/world and continuing real scene render.");
                    var afterZoom = EngineOutputSample.Read(game.GraphicsContext.CommandList, native.Target);
                    zoomOutput = beforeZoom!.Compare(afterZoom, 0, 0, afterZoom.Width, afterZoom.Height);
                    if (zoomOutput.Value.ChangedPixels == 0) return false;
                    receipts.Add(new { contract = "native-ui-wheel-camera-and-gpu-output", beforeSize = uiBaseline.CameraSize, afterSize = native.CameraSize,
                        zoomOutput.Value.ChangedPixels, sceneCameraUnchanged = true, sceneRenderingAdvanced = true });
                    phase = 9; return false;
                case 9:
                    if (click("ui/zoom/" + ((UIDocument)ui.Document).Asset.Id + "/zoom-default")) phase = 10;
                    return false;
                case 10:
                    if (UiNative() is not { } restored || Math.Abs(restored.CameraSize - uiBaseline!.CameraSize) > 0.00001f) return false;
                    Require(game.InspectDocumentGroupParity(scene.Id)?.Camera == sceneBaseline!.Camera,
                        "Actual UI Reset Zoom must preserve the independent scene camera.");
                    receipts.Add(new { contract = "actual-native-reset-zoom-control", restored.CameraSize });
                    returning = true; phase = 1; return false;
                case 12:
                    if (!game.NativeDocumentRestorationSettled || game.DocumentGroupPanel.Model.Find(ui) != game.DocumentGroupPanel.Model.Find(scene)
                        || game.DocumentGroupPanel.Model.Groups.Count() != originalGroups) return false;
                    if (!game.VerifyRecoveryAcceptanceNativeTab(scene.Path, out _)) { click("document-tab/" + scene.Id); return false; }
                    if (!game.VerificationScenePresented) return false;
                    Require(ReferenceEquals(scene.State, sceneOwner) && ReferenceEquals(ui.State, uiOwner) && sceneOwner.IsPublished && uiOwner.IsPublished
                        && game.sessionHistory!.History.Position == historyPosition
                        && game.sessionHistory.Stack.RetrieveAllTransactions().Take(historyPosition).Select(transaction => transaction.Id).SequenceEqual(appliedIds)
                        && File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBytes) && File.ReadAllBytes(ui.Path).AsSpan().SequenceEqual(uiBytes),
                        "Grouping and native camera/selection input must preserve canonical graphs, applied history and authored files.");
                    receipts.Add(new { contract = "actual-previous-menu-and-scene-tab-baseline-restoration", originalGroups, historyPosition });
                    phase = 13; return true;
                case 13: return true;
            }
            return false;
        }
    }
}
