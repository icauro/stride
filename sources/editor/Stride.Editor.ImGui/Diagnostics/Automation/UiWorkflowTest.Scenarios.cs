using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Rendering.ProceduralModels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal enum UiWorkflowScenario { Legacy, U01ProjectLoading, U01FailureRecovery, U02Hierarchy, U03Inspector, U03ReferencesCollections, U03NumericBoundaries, U04AssetBrowser, U05DocumentClose, U05SaveAll, U06MaterialEditor, U07Layout, U07Output, U07ErrorNavigation, U07LayoutWrite, U07LayoutRead, U06AnimationEditor, U06SpriteEditor, U06UIEditor, U06CompositorEditor }

internal sealed partial class UiWorkflowTest
{
    private UiWorkflowScenario scenario;
    private float independentOriginalX;
    private SceneDocument? saveAllScene;
    private ProceduralAssetDocument? saveAllAsset;
    private int replacementSaveStage = int.MinValue;
    private long loadOutputSequence;
    private Guid[] preservedEntityIds = [];
    private string? preservedCompiledSceneUrl;
    internal string ScenarioScope => scenario switch
    {
        UiWorkflowScenario.U01ProjectLoading => "U01 subset: initial cancellation/retry, replacement cancellation and clean project close; controlled load failure remains a gap.",
        UiWorkflowScenario.U01FailureRecovery => "U01 failure case: real replacement compiler failure retains the usable prior session, restore fixture source and retry publishes compiled content; compiler/backend families remain coverage limits.",
        UiWorkflowScenario.U05DocumentClose => "U05 subset: single-scene dirty close Cancel/Save-conflict/retry or Discard with independently read saved asset; multiple documents and Save All remain gaps.",
        UiWorkflowScenario.U05SaveAll => "U05 subset: retain dirty scene while activating/editing a procedural asset, Ctrl+S Save All and independently deserialize both assets; arbitrary document families/per-document close remain gaps.",
        UiWorkflowScenario.U02Hierarchy => "U02: create/select/search/rename, menu reparent, subtree Delete Cancel/confirm/undo and persisted authored/runtime identities; pointer drag/drop reparent remains a gap.",
        UiWorkflowScenario.U03Inspector => "U03 subset: representative transform vector axis and light scalar widget edits, actual runtime values, undo/redo and independent persistence; references/collections/read-only copy remain gaps.",
        UiWorkflowScenario.U03ReferencesCollections => "U03 reference/collection case: actual entity-reference selection, read-only select/copy, list reorder/remove/add and undo/redo through controls, stable item IDs and independent persistence; remaining value families need coverage.",
        UiWorkflowScenario.U03NumericBoundaries => "Actual unsigned 64-bit numeric boundaries and individual flags controls, runtime equality, native history and independent persisted values; aggregate/mixed-owner controls remain outside this scenario.",
        UiWorkflowScenario.U04AssetBrowser => "U04: name filtering, actual asset selection/compiled preview/canonical editor, inline rename/path move with stable asset IDs/references and independent persistence; folder drag/drop and cross-package movement remain limits.",
        UiWorkflowScenario.U07Layout => "U07 subset: native tool-window separation and usable bounds, hide Property grid then Reset layout through menu, preserve authored owner and camera; layout persistence/restart/output filtering/error navigation remain gaps.",
        UiWorkflowScenario.U07Output => "U07 output case: actual compiler-log Info filtering, committed search and next-match selection via controls; diagnostic source navigation and persisted layout/restart remain gaps.",
        UiWorkflowScenario.U07ErrorNavigation => "U07 diagnostic case: genuine fixture compiler error, search its native rendered glyph and double-click through output navigation to the canonical source document/caret.",
        UiWorkflowScenario.U07LayoutWrite => "U07 restart writer: genuine tool-tab drag, native layout persistence and saved expectation for a separate restart; reader result is required for acceptance.",
        UiWorkflowScenario.U07LayoutRead => "U07 restart reader: a separate editor process loads the saved native layout and verifies the actual tool-window dock topology.",
        UiWorkflowScenario.U06MaterialEditor => "U06 subset: actual material glossiness widget edit, undo/redo, save and independent reload; animation/sprite/UI/compositor editor families remain gaps.",
        UiWorkflowScenario.U06AnimationEditor => "U06 animation: real root-motion checkbox, undo/redo, save and independent native animation reload preserving crop/range/source/references; nested crop editing remains a capability gap.",
        UiWorkflowScenario.U06SpriteEditor => "U06 sprite: representative authored sprite editor operation with native persistence assertions.",
        UiWorkflowScenario.U06UIEditor => "U06 UI: representative authored UI editor operation with native persistence assertions.",
        UiWorkflowScenario.U06CompositorEditor => "U06 compositor: actual ForwardRenderer LightProbes checkbox, undo/redo, save and independent native compositor asset reload.",
        _ => "Legacy combined dependent workflow; not an independently isolated checklist acceptance scenario.",
    };

    internal void SelectScenario(UiWorkflowScenario value)
    {
        if (stage != 0 || checks.Count != 0) throw new InvalidOperationException("Select the scenario before running input.");
        scenario = value;
        if (value == UiWorkflowScenario.U06AnimationEditor) PrepareAnimationAuthoringScenarioFixture();
        if (value == UiWorkflowScenario.U06SpriteEditor) PrepareSpecializedAuthoredFixture(true);
        if (value == UiWorkflowScenario.U06UIEditor) PrepareSpecializedAuthoredFixture(false);
        if (value != UiWorkflowScenario.Legacy) stage = 10000;
    }

    private void UpdateIndependentScenario()
    {
        if (stage is >= 12000 and <= 12099) { UpdateInspectorGroupReset(); return; }
        if (scenario is UiWorkflowScenario.U01FailureRecovery or UiWorkflowScenario.U07ErrorNavigation)
        {
            // The controlled source mutation invokes both real file watchers.
            // A queued background prompt is not an active modal: act only on
            // visible native controls, preserving whichever modal owns input.
            var notice = game.Controls.Centers.ContainsKey("external-change/keep") ? "external-change/keep"
                : game.Controls.Centers.ContainsKey("script-reload/dont-reload") ? "script-reload/dont-reload" : null;
            if (notice != null)
            {
                if (Click(notice))
                {
                    if (stage is 10402 or 10406) stage--;
                    // A notice can arrive after splitter aim but before press;
                    // its genuine UI action moves the pointer away from the gap.
                    else if (stage is >= 11340 and <= 11342) stage = 11300;
                }
                return;
            }
        }
        if (stage is >= 10500 and <= 10699) { UpdateHierarchyInspectorScenario(); return; }
        if (stage is >= 10700 and <= 10799) { UpdateAssetBrowserScenario(); return; }
        if (stage is >= 10800 and <= 10899) { UpdateLayoutScenario(); return; }
        if (stage is >= 10900 and <= 10999) { UpdateMaterialEditorScenario(); return; }
        if (stage is >= 11000 and <= 11099) { UpdateReferencesCollectionsScenario(); return; }
        if (stage is >= 10400 and <= 10499) { UpdateLoadFailureRecoveryScenario(); return; }
        if (stage is >= 11100 and <= 11199) { UpdateOutputScenario(); return; }
        if (stage is >= 11200 and <= 11299) { UpdateLayoutRestartScenario(); return; }
        if (stage is >= 11300 and <= 11399) { UpdateErrorNavigationScenario(); return; }
        if (stage is >= 11400 and <= 11499) { UpdateAnimationAuthoringScenario(); return; }
        if (stage is >= 11700 and <= 11799) { UpdateSpriteEditorScenario(); return; }
        if (stage is >= 11800 and <= 11899) { UpdateUIEditorScenario(); return; }
        if (stage is >= 11900 and <= 11999) { UpdateCompositorEditorScenario(); return; }
        if (stage is >= 13300 and <= 13399) { UpdateNumericBoundariesScenario(); return; }
        var io = Gui.GetIO();
        switch (stage)
        {
            case 10000: if (Click("toolbar/open")) Next(); break;
            case 10001:
                if (Click("project/open"))
                { loadOutputSequence = game.CaptureWorkflowRecentOutput().LastOrDefault()?.Sequence ?? 0; Next(); }
                break;
            case 10002:
                if (!game.WorkflowProjectPublicationPending && game.Document == null
                    && game.CaptureWorkflowCommandFailureAfter(loadOutputSequence) is { } loadFailure)
                    throw new InvalidOperationException("Project load failed before publishing its document: " + loadFailure.Text);
                if (scenario == UiWorkflowScenario.U01ProjectLoading && !cancelledFirstLoad)
                {
                    if (game.ProjectBuildProcessId is { } process && Click("loading/cancel"))
                    { cancelledCompiler = process; Next(); }
                }
                else if (!game.IsProjectLoading && game.Document != null && game.UsesCompiledProjectContent
                    && game.CaptureInspectionSnapshot().Viewport.HasRuntimeScene)
                {
                    CaptureInspectorGroupBaseline();
                    if (scenario == UiWorkflowScenario.U02Hierarchy) { stage = 10500; break; }
                    if (scenario is UiWorkflowScenario.U01FailureRecovery or UiWorkflowScenario.U07ErrorNavigation) { stage = 10400; break; }
                    if (scenario == UiWorkflowScenario.U03Inspector) { stage = 10600; break; }
                    if (scenario == UiWorkflowScenario.U03ReferencesCollections) { stage = 11000; break; }
                    if (scenario == UiWorkflowScenario.U03NumericBoundaries) { stage = 13300; break; }
                    if (scenario == UiWorkflowScenario.U04AssetBrowser) { stage = 10700; break; }
                    if (scenario == UiWorkflowScenario.U07Layout) { stage = 10800; break; }
                    if (scenario == UiWorkflowScenario.U07Output) { stage = 11100; break; }
                    if (scenario is UiWorkflowScenario.U07LayoutWrite or UiWorkflowScenario.U07LayoutRead) { stage = 11200; break; }
                    if (scenario == UiWorkflowScenario.U06MaterialEditor) { stage = 10900; break; }
                    if (scenario == UiWorkflowScenario.U06AnimationEditor) { stage = 11400; break; }
                    if (scenario == UiWorkflowScenario.U06SpriteEditor) { stage = 11700; break; }
                    if (scenario == UiWorkflowScenario.U06UIEditor) { stage = 11800; break; }
                    if (scenario == UiWorkflowScenario.U06CompositorEditor) { stage = 11900; break; }
                    if (scenario == UiWorkflowScenario.U01ProjectLoading)
                    {
                        preservedDocument = game.Document; preservedPreview = game.TexturePreview;
                        preservedEntityIds = preservedDocument.Asset.Hierarchy.Parts.Keys.ToArray();
                        preservedCompiledSceneUrl = game.CaptureInspectionSnapshot().Viewport.SceneUrl;
                        checks.Add("Retry publishes the real compiled fixture session");
                        stage = 10100;
                    }
                    else if (Click("entity/Sphere")) { stage = 10004; wait = 2; }
                }
                break;
            case 10003:
                if (!game.IsProjectLoading && ProcessExited(cancelledCompiler))
                {
                    Assert(game.Document == null, "Cancelled initial load must not publish a document");
                    checks.Add("Initial cancellation terminates the real compiler without publishing a session");
                    cancelledFirstLoad = true; stage = 10000;
                }
                break;
            case 10100: if (Click("toolbar/open")) Next(); break;
            case 10101:
                if (Click("project/open"))
                { loadOutputSequence = game.CaptureWorkflowRecentOutput().LastOrDefault()?.Sequence ?? 0; Next(); }
                break;
            case 10102:
                if (TrySaveReplacementPrompt()) break;
                if (!game.WorkflowProjectPublicationPending
                    && game.CaptureWorkflowCommandFailureAfter(loadOutputSequence) is { } replacementFailure)
                    throw new InvalidOperationException("Project replacement failed before cancellation could be exercised: " + replacementFailure.Text);
                if (game.ProjectBuildProcessId is { } replacement && Click("loading/cancel"))
                { cancelledCompiler = replacement; Next(); }
                break;
            case 10103:
                if (!game.IsProjectLoading && ProcessExited(cancelledCompiler))
                {
                    Assert(ReferenceEquals(game.Document, preservedDocument) && ReferenceEquals(game.TexturePreview, preservedPreview),
                        "Replacement cancellation must retain the prior document and preview owners");
                    var retained = game.CaptureInspectionSnapshot();
                    Assert(game.UsesCompiledProjectContent && retained.Project.HasCompiledContent
                        && retained.Viewport.HasRuntimeScene && retained.Viewport.UsesAuthoredCompositor
                        && retained.Viewport.SceneUrl == preservedCompiledSceneUrl && !game.Document!.IsDirty,
                        "Replacement cancellation must retain usable compiled content and clean authored data");
                    Assert(game.Document.Asset.Hierarchy.Parts.Keys.Order().SequenceEqual(preservedEntityIds.Order())
                        && preservedEntityIds.All(id => game.HasRuntimeEntity(id,
                            game.Document.Asset.Hierarchy.Parts[id].Entity.Transform.Parent?.Entity.Id)),
                        "Replacement cancellation must retain authored entity identities and their actual runtime hierarchy");
                    checks.Add("Replacement cancellation terminates compiler and preserves the usable prior session");
                    Next();
                }
                break;
            case 10104: if (Click("menu/project")) Next(); break;
            case 10105: if (ClickPopupControl("project/close")) Next(); break;
            case 10106:
                if (TrySaveReplacementPrompt()) break;
                var closed = game.CaptureInspectionSnapshot();
                if (!game.IsProjectLoading && !closed.Project.HasActivePackage && closed.Documents.Count == 0)
                {
                    Assert(game.Document == null && !closed.Project.HasCompiledContent && closed.Project.AssetCount == 0
                        && closed.Selection.AssetPath == null && closed.Selection.EntityIds.Count == 0,
                        "Close project must clear session, compiled content, documents and selection");
                    Assert(preservedPreview == null || preservedPreview.IsDisposed,
                        "Close project must release the prior texture preview");
                    checks.Add("Close project through its real menu clears session/content/documents/selection");
                    Passed = Finished = true;
                    Result = "UI U01 subset PASS: " + string.Join("; ", checks) + ". " + ScenarioScope;
                }
                break;
            case 10004:
                if (game.Document?.Selected?.Name == "Sphere")
                {
                    independentOriginalX = game.Document.Selected.Transform.Position.X;
                    Assert(independentOriginalX != 3.5f, "Close fixture must start with a different saved position");
                    if (Click("Position/0")) Next();
                }
                break;
            case 10005: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10006:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "3.5") io.AddInputCharacter(character);
                Next(); break;
            case 10007: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10008: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10009:
                AssertPosition(3.5f); Assert(game.Document!.IsDirty, "Input edit must create dirty authored data");
                if (scenario == UiWorkflowScenario.U05SaveAll) { saveAllScene = game.Document; stage = 10200; break; }
                RequestNativeClose(); Next(); break;
            case 10010: if (game.ClosePromptOpen && Click("close/cancel")) Next(); break;
            case 10011:
                Assert(!game.ClosePromptOpen && !game.IsExiting && game.Document!.IsDirty,
                    "Cancel must leave dirty data and the editor open");
                AssertPosition(3.5f); checks.Add("Native close Cancel preserves dirty data");
                RequestNativeClose(); Next(); break;
            case 10012:
                if (game.ClosePromptOpen)
                {
                    if (discardClose) { if (Click("close/discard")) Next(); }
                    else
                    {
                        if (closeDiskBackup == null)
                        {
                            closeDiskBackup = File.ReadAllBytes(game.Document!.Path);
                            File.AppendAllText(game.Document.Path, "\n# External fixture change for U05\n");
                        }
                        if (Click("close/save")) Next();
                    }
                }
                break;
            case 10013:
                if (!discardClose)
                {
                    Assert(game.ClosePromptOpen && !game.IsExiting && game.Document!.IsDirty,
                        "Failed close Save must leave recoverable dirty data and prompt");
                    AssertPosition(3.5f); checks.Add("Save conflict retains edits and permits retry");
                    File.WriteAllBytes(game.Document.Path, closeDiskBackup!);
                    if (Click("close/save")) Next();
                }
                break;
            case 10014: break; // The existing native approved-close observer verifies disk independently.
            case 10200: if (Click("asset/filter")) Next(); break;
            case 10201: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10202:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "Sphere") io.AddInputCharacter(character);
                Next(); break;
            case 10203: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10204: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10205: if (Click("asset/Sphere")) Next(); break;
            case 10206:
                if (game.ModelPreview is { RenderCount: > 0 } && Click("asset/edit")) Next();
                break;
            case 10207:
                if (game.AssetDocument is { } asset && Click("asset-editor/field/Radius"))
                {
                    saveAllAsset = asset;
                    Assert(ReferenceEquals(game.Document, saveAllScene) && saveAllScene!.IsDirty,
                        "Activating another authored asset must retain the same dirty scene");
                    Assert(asset.Asset.Type is SphereProceduralModel sphere && sphere.Radius != 1.25f,
                        "Save All fixture requires a distinct initial sphere radius");
                    Next();
                }
                break;
            case 10208: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10209:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "1.25") io.AddInputCharacter(character);
                Next(); break;
            case 10210: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10211: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10212:
                Assert(saveAllScene!.IsDirty && saveAllAsset!.IsDirty && saveAllAsset.Asset.Type is SphereProceduralModel { Radius: 1.25f },
                    "Both independently authored documents must remain dirty before Save All");
                checks.Add("Asset activation and widget edit preserve both dirty authored documents");
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); Next(); break;
            case 10213: io.AddKeyEvent(ImGuiKey.S, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 10214:
                if (!saveAllScene!.IsDirty && !saveAllAsset!.IsDirty)
                {
                    Assert(ReferenceEquals(game.Document, saveAllScene) && ReferenceEquals(game.AssetDocument, saveAllAsset),
                        "Save All must retain the same authored document owners");
                    var sceneDisk = AssetFileSerializer.Load<SceneAsset>(saveAllScene.Path).Asset;
                    var assetDisk = AssetFileSerializer.Load<ProceduralModelAsset>(saveAllAsset.Path).Asset;
                    Assert(sceneDisk.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Transform.Position.X == 3.5f
                        && assetDisk.Type is SphereProceduralModel { Radius: 1.25f },
                        "Save All must independently persist both edited asset files");
                    checks.Add("Ctrl+S Save All persists scene and procedural asset without replacing owners");
                    Passed = Finished = true;
                    Result = "UI U05 Save All subset PASS: " + string.Join("; ", checks) + ". " + ScenarioScope;
                }
                break;
        }
    }

    private bool TrySaveReplacementPrompt()
    {
        if (!game.Controls.Centers.ContainsKey("replace/save")) return false;
        if (replacementSaveStage == stage)
        {
            // A failed supported Save is a real capability failure, not a reason
            // to authorize discard or hammer the same command indefinitely.
            throw new InvalidOperationException("Save and continue left the replacement prompt open: " + game.StatusMessage);
        }
        if (Click("replace/save"))
        {
            replacementSaveStage = stage;
            RecordAction("replacement-save-request", "Save and continue");
        }
        return true;
    }
}
