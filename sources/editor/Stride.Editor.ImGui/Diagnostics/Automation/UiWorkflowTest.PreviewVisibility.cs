using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Matrix = Stride.Core.Mathematics.Matrix;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private readonly List<object> previewVisibilityEvidence = [];
    private TextureAssetPreview? visibilityTexture;
    private ModelAssetPreview? visibilityModel;
    private Matrix visibilityCamera, visibilitySceneCamera;
    private int visibilityReturnStage, visibilityRenderCount;
    private double visibilityRenderAge;
    private string? visibilitySelection;

    private void StartPreviewVisibilityCheck(int returnStage)
    {
        visibilityTexture = game.TexturePreview; visibilityModel = game.ModelPreview;
        Assert(visibilityTexture != null || visibilityModel != null, "Visibility check requires a loaded preview");
        visibilityCamera = visibilityModel?.CameraMatrix ?? default;
        visibilitySceneCamera = game.SceneCameraView; visibilitySelection = game.SelectedAssetPath;
        visibilityReturnStage = returnStage; stage = 800; wait = 2;
    }

    private int PreviewRenderCount => visibilityModel?.RenderCount ?? visibilityTexture!.RenderCount;
    private int PreviewDrawCalls => visibilityModel?.DrawCalls ?? visibilityTexture!.DrawCalls;
    private bool PreviewVisible => visibilityModel?.Visible ?? visibilityTexture!.Visible;

    private void AssertPreviewIdentity()
    {
        Assert(ReferenceEquals(game.ModelPreview, visibilityModel) && ReferenceEquals(game.TexturePreview, visibilityTexture)
            && visibilityModel?.IsDisposed != true && visibilityTexture?.IsDisposed != true,
            "Hiding a preview must retain the same engine/content/GPU resource owner");
        Assert(game.SelectedAssetPath == visibilitySelection && game.SceneCameraView == visibilitySceneCamera
            && !game.Document!.IsDirty && !game.Document.CanUndo,
            "Preview visibility must preserve selection, authored scene, history and scene camera");
        if (visibilityModel != null) Assert(visibilityModel.CameraMatrix == visibilityCamera, "Preview visibility must preserve model camera framing");
    }

    private void UpdatePreviewVisibilityCheck()
    {
        switch (stage)
        {
            case 800:
                Assert(PreviewVisible, "The selected asset preview must initially be visible");
                visibilityRenderCount = PreviewRenderCount; stage = 801; wait = 60; break;
            case 801:
                if (visibilityTexture != null)
                    Assert(PreviewRenderCount == visibilityRenderCount && PreviewDrawCalls == 0,
                        "An unchanged visible texture preview must reuse its rendered target without engine redraws");
                else Assert(PreviewRenderCount > visibilityRenderCount && PreviewDrawCalls > 0, "Visible model preview must continue engine rendering");
                if (Click("menu/view")) { stage = 802; wait = 2; } break;
            case 802: if (Click("menu/view/asset-preview")) { stage = 803; wait = 2; } break;
            case 803:
                Assert(!PreviewVisible && PreviewDrawCalls == 0, "View menu must hide the asset preview and stop submissions");
                visibilityRenderCount = PreviewRenderCount; visibilityRenderAge = visibilityModel?.RenderAgeSeconds ?? 0;
                stage = 804; wait = 60; break;
            case 804:
                Assert(!PreviewVisible && PreviewRenderCount == visibilityRenderCount && PreviewDrawCalls == 0,
                    "A hidden preview must submit no engine draws across the sampled interval");
                if (visibilityModel != null) Assert(visibilityModel.RenderAgeSeconds == visibilityRenderAge,
                    "Hidden time must not advance visible model exposure warmup");
                AssertPreviewIdentity();
                var assetName = (visibilityModel?.Name ?? visibilityTexture!.Name).Split('/')[^1];
                if (Click("asset/" + assetName)) { stage = 812; wait = 2; } break;
            case 812:
                if (visibilityModel != null)
                {
                    Assert(visibilityModel.IsDisposed && game.ModelPreview != null && !ReferenceEquals(visibilityModel, game.ModelPreview),
                        "Reopening a hidden model must replace and dispose its prior resources");
                    visibilityModel = game.ModelPreview!; visibilityCamera = visibilityModel.CameraMatrix;
                }
                else
                {
                    Assert(visibilityTexture!.IsDisposed && game.TexturePreview != null && !ReferenceEquals(visibilityTexture, game.TexturePreview),
                        "Reopening a hidden texture must replace and dispose its prior resources");
                    visibilityTexture = game.TexturePreview!;
                }
                Assert(!PreviewVisible && PreviewRenderCount == 0 && PreviewDrawCalls == 0,
                    "Selecting an asset must respect the hidden-panel preference and defer preview rendering");
                visibilityRenderCount = 0; visibilityRenderAge = 0;
                stage = 813; wait = 60; break;
            case 813:
                Assert(!PreviewVisible && PreviewRenderCount == 0 && PreviewDrawCalls == 0,
                    "A newly selected hidden asset must remain unrendered until the user restores the panel");
                AssertPreviewIdentity();
                if (Click("menu/view")) { stage = 805; wait = 2; } break;
            case 805:
                if (visibilityModel != null) game.CapturePreviewVisibilityCheckpoint();
                if (Click("menu/view/asset-preview")) { stage = 806; wait = 2; } break;
            case 806:
                Assert(PreviewVisible, "View menu must restore the selected asset preview");
                AssertPreviewIdentity(); visibilityRenderCount = PreviewRenderCount;
                stage = 807; wait = 60; break;
            case 807:
                AssertPreviewIdentity();
                if (visibilityModel != null)
                {
                    Assert(PreviewRenderCount > visibilityRenderCount && PreviewDrawCalls > 0
                        && visibilityModel.RenderAgeSeconds > visibilityRenderAge, "Restored model preview must resume draws and visible warmup");
                    FinishPreviewVisibilityCheck();
                }
                else
                {
                    Assert(PreviewRenderCount == visibilityRenderCount && PreviewDrawCalls == 0,
                        "Restored unchanged texture must retain its cached output after layout settles");
                    if (Click("preview/mip")) { stage = 808; wait = 2; }
                }
                break;
            case 808: if (Click("preview/mip/1")) { stage = 809; wait = 2; } break;
            case 809:
                Assert(visibilityTexture!.Mip == 1 && PreviewRenderCount > visibilityRenderCount,
                    "Restored texture controls must invalidate and redraw the engine preview");
                if (Click("preview/mip")) { stage = 810; wait = 2; } break;
            case 810: if (Click("preview/mip/0")) { stage = 811; wait = 2; } break;
            case 811:
                Assert(visibilityTexture!.Mip == 0, "Visibility workflow must restore the texture's original mip");
                AssertPreviewIdentity(); FinishPreviewVisibilityCheck(); break;
        }
    }

    private void FinishPreviewVisibilityCheck()
    {
        var kind = visibilityModel != null ? "model" : "texture";
        previewVisibilityEvidence.Add(new { kind, hiddenFrameWait = 60, hiddenRenderCountDelta = 0,
            retainedResourceOwner = true, cameraAndDocumentPreserved = true,
            hiddenAssetReopenDefersRendering = true,
            unchangedTextureUsesCachedOutput = visibilityTexture != null, modelRenderingResumed = visibilityModel != null });
        checks.Add($"{kind} preview View hide/restore retains resources and state, stops hidden rendering and resumes correctly");
        stage = visibilityReturnStage; wait = 2;
    }
}
