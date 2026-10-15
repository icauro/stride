using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Rendering.ProceduralModels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private bool assetEditorWorkflowVerified;
    private SceneDocument? assetSceneDocument;
    private ProceduralAssetDocument? editingAssetDocument;
    private ModelAssetPreview? assetOldPreview;
    private Matrix assetSceneCamera;
    private int assetCancelledProcess;
    private string? assetBuildFailureProbe;
    private object? assetEditingEvidence;
    private void AssertAssetRadius(float expected)
    {
        Assert(game.AssetDocument!.Asset.Type is SphereProceduralModel sphere && sphere.Radius == expected, "Asset UI must mutate the real authored procedural property");
        Assert(ReferenceEquals(game.Document, assetSceneDocument) && game.Document!.IsDirty && game.Document.CanUndo
            && game.Document.Selected!.Transform.Position.X == 3.25f && game.SceneCameraView == assetSceneCamera,
            "Asset edits/builds must preserve the actual scene document, unsaved transform, history and camera");
    }
    private void UpdateAssetEditorWorkflow()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 1100: if (Click("asset/filter")) { stage = 1101; wait = 2; } break;
            case 1101: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 1102; wait = 2; break;
            case 1102:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "Sphere") io.AddInputCharacter(c); stage = 1103; wait = 2; break;
            case 1103: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 1104; wait = 2; break;
            case 1104: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 1105; wait = 2; break;
            case 1105: if (Click("asset/Sphere")) { stage = 1106; wait = 2; } break;
            case 1106:
                if (game.ModelPreview is { RenderAgeSeconds: >= 5 } preview)
                {
                    assetOldPreview = preview; assetSceneDocument = game.Document; assetSceneCamera = game.SceneCameraView;
                    if (Click("Position/0")) { stage = 1107; wait = 2; }
                }
                break;
            case 1107: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 1108; wait = 2; break;
            case 1108:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "3.25") io.AddInputCharacter(c); stage = 1109; wait = 2; break;
            case 1109: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 1110; wait = 2; break;
            case 1110: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 1111; wait = 2; break;
            case 1111: if (Click("model-preview/edit")) { stage = 1112; wait = 2; } break;
            case 1112:
                editingAssetDocument = game.AssetDocument;
                if (Click("asset-editor/field/Radius")) { stage = 1113; wait = 2; } break;
            case 1113: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 1114; wait = 2; break;
            case 1114:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "1.25") io.AddInputCharacter(c); stage = 1115; wait = 2; break;
            case 1115: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 1116; wait = 2; break;
            case 1116: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 1117; wait = 2; break;
            case 1117:
                AssertAssetRadius(1.25f); Assert(game.AssetDocument!.IsDirty, "Asset edits must expose dirty state");
                RequestNativeClose(); stage = 1141; wait = 2; break;
            case 1141:
                if (game.ClosePromptOpen && Click("close/cancel")) { stage = 1142; wait = 2; } break;
            case 1142:
                AssertAssetRadius(1.25f);
                Assert(ReferenceEquals(game.AssetDocument, editingAssetDocument) && game.AssetDocument!.IsDirty,
                    "Native editor-close Cancel must preserve both unsaved asset and scene documents");
                if (Click("asset-editor/close")) { stage = 1118; wait = 2; } break;
            case 1118: if (Click("asset-discard/cancel")) { stage = 1119; wait = 2; } break;
            case 1119:
                Assert(ReferenceEquals(game.AssetDocument, editingAssetDocument) && game.AssetDocument!.IsDirty, "Cancelled asset close must preserve the same document and edits");
                if (Click("asset-editor/undo")) { stage = 1120; wait = 2; } break;
            case 1120: AssertAssetRadius(0.5f); if (Click("asset-editor/redo")) { stage = 1121; wait = 2; } break;
            case 1121:
                AssertAssetRadius(1.25f);
                if (assetBuildFailureProbe == null)
                {
                    var root = Directory.GetParent(Path.GetDirectoryName(game.AssetDocument!.Path)!)!.FullName;
                    Assert(root.Contains("Stride-ImGui-UI", StringComparison.OrdinalIgnoreCase), "Build failure injection must stay in the disposable fixture");
                    assetBuildFailureProbe = Path.Combine(root, "AssetBuildFailureProbe.cs");
                    File.WriteAllText(assetBuildFailureProbe, "#error Intentional asset preview build failure fixture\n");
                }
                if (Click("asset-editor/update")) { stage = 1140; wait = 2; }
                break;
            case 1140:
                if (!game.IsAssetPreviewUpdating)
                {
                    Assert(game.StatusMessage.Contains("failed", StringComparison.OrdinalIgnoreCase) && ReferenceEquals(game.ModelPreview, assetOldPreview) && !assetOldPreview!.IsDisposed,
                        "Real compiler failure must retain old preview and report the failure");
                    AssertAssetRadius(1.25f); File.Delete(assetBuildFailureProbe!);
                    if (Click("asset-editor/update")) { stage = 1122; wait = 2; }
                }
                break;
            case 1122:
                if (game.IsAssetPreviewUpdating && game.AssetPreviewBuildProcessId is { } process && !ProcessExited(process) && Click("asset-update/cancel"))
                { assetCancelledProcess = process; stage = 1123; wait = 2; }
                break;
            case 1123:
                if (!game.IsAssetPreviewUpdating)
                {
                    Assert(ProcessExited(assetCancelledProcess) && ReferenceEquals(game.ModelPreview, assetOldPreview) && !assetOldPreview!.IsDisposed,
                        "Cancelled preview compilation must stop its owned process and retain the original preview");
                    AssertAssetRadius(1.25f);
                    Assert(((SphereProceduralModel)AssetFileSerializer.Load<ProceduralModelAsset>(game.AssetDocument!.Path).Asset.Type).Radius == 1.25f,
                        "Cancellation must retain the saved authored asset");
                    if (Click("asset-editor/update")) { stage = 1124; wait = 2; }
                }
                break;
            case 1124:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } updated && !ReferenceEquals(updated, assetOldPreview))
                {
                    AssertAssetRadius(1.25f);
                    Assert(assetOldPreview!.IsDisposed && Math.Abs(updated.FramingBounds.Radius - 1.25f) < 0.0001f
                        && Math.Abs(game.RuntimeAssetRadius(updated.Name) - 1.25f) < 0.0001f && updated.VertexBytes > 0 && updated.DrawCalls > 0,
                        $"Actual newly compiled GPU model must replace preview/scene: disposed={assetOldPreview.IsDisposed}, preview radius={updated.FramingBounds.Radius}, scene radius={game.RuntimeAssetRadius(updated.Name)}, bytes={updated.VertexBytes}, draws={updated.DrawCalls}, url={updated.Name}");
                    game.CaptureUpdatedAssetCheckpoint();
                    assetEditingEvidence = new { assetUrl = updated.Name, authoredRadius = 1.25f, previewHalfExtent = updated.FramingBounds.Radius,
                        sceneHalfExtent = game.RuntimeAssetRadius(updated.Name), vertexBytes = updated.VertexBytes, drawCalls = updated.DrawCalls,
                        sceneDocumentRetained = ReferenceEquals(game.Document, assetSceneDocument), sceneDirty = game.Document!.IsDirty,
                        scenePositionX = game.Document.Selected!.Transform.Position.X, sceneCanUndo = game.Document.CanUndo,
                        cameraPreserved = game.SceneCameraView == assetSceneCamera, cancelledProcess = assetCancelledProcess };
                    if (Click("model-preview/edit")) { stage = 1125; wait = 2; }
                }
                break;
            case 1125:
                Assert(ReferenceEquals(game.AssetDocument, editingAssetDocument) && game.AssetDocument!.CanUndo, "Opening the existing editor must preserve asset history after refresh");
                io.AddMousePosEvent(700, 120); stage = 1126; wait = 2; break;
            case 1126:
                game.CaptureAssetEditorCheckpoint(); if (Click("asset-editor/undo")) { stage = 1127; wait = 2; } break;
            case 1127: AssertAssetRadius(0.5f); if (Click("asset-editor/update")) { stage = 1128; wait = 2; } break;
            case 1128:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } restored)
                {
                    AssertAssetRadius(0.5f);
                    Assert(Math.Abs(restored.FramingBounds.Radius - 0.5f) < 0.0001f && Math.Abs(game.RuntimeAssetRadius(restored.Name) - 0.5f) < 0.0001f,
                        "Asset undo/save/recompile must restore the actual preview and scene geometry");
                    Assert(game.RefreshedModelOwners == 1 && game.RetiredModelOwners == 0, "Superseded refreshed content owners must retire after model processors advance");
                    if (Click("model-preview/edit")) { stage = 1129; wait = 2; }
                }
                break;
            case 1129: if (Click("asset-editor/close")) { stage = 1130; wait = 2; } break;
            case 1130:
                Assert(game.AssetDocument == null && ReferenceEquals(game.Document, assetSceneDocument), "Clean asset close must retain the original scene document");
                if (Click("toolbar/undo")) { stage = 1131; wait = 2; } break;
            case 1131:
                AssertPosition(2.5f); Assert(!game.Document!.IsDirty, "Scene undo history must remain usable after asset rebuilds");
                assetEditorWorkflowVerified = true;
                checks.Add("Docked procedural asset editor, radius undo/redo, dirty close Cancel, real compiler failure/cancellation/retry, preview/scene GPU model refresh/restoration and retired owners with scene edits/history/camera preserved");
                priorModelPreview = game.ModelPreview;
                if (Click("asset/filter")) { stage = 718; wait = 2; }
                break;
        }
    }
}
