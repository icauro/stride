using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Rendering.ProceduralModels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private IProceduralModel? beforeTypeChange;
    private Stride.Core.Assets.AssetId beforeTypeAssetId;
    private int cubeAxis;
    private void UpdateModelTypeWorkflow()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 400: if (Click("model-preview/edit")) { stage = 401; wait = 2; } break;
            case 401:
                beforeTypeChange = game.AssetDocument!.Asset.Type; beforeTypeAssetId = game.AssetDocument.Asset.Id;
                if (Click("asset-editor/type")) { stage = 402; wait = 2; } break;
            case 402: if (Click("asset-editor/type/CubeProceduralModel")) { stage = 403; wait = 2; } break;
            case 403:
                Assert(game.AssetDocument!.Asset.Type is CubeProceduralModel { Size: var size } && size == Vector3.One
                    && game.AssetDocument.Asset.Id == beforeTypeAssetId, "Type selection must create real default Cube parameters without changing asset identity");
                if (Click("asset-editor/undo")) { stage = 404; wait = 2; } break;
            case 404:
                Assert(ReferenceEquals(game.AssetDocument!.Asset.Type, beforeTypeChange)
                    && game.AssetDocument.Fields.Single(field => field.Name == "Size").Type == typeof(Vector2), "Type Undo must restore the prior object and property schema");
                if (Click("asset-editor/redo")) { stage = 405; wait = 2; } break;
            case 405:
                Assert(game.AssetDocument!.Asset.Type is CubeProceduralModel && game.AssetDocument.Fields.Single(field => field.Name == "Size").Type == typeof(Vector3),
                    "Type Redo must restore Cube and its graph fields");
                cubeAxis = 0; stage = 410; wait = 2; break;
            case 410: if (Click("asset-editor/field/Size/axis/" + "XYZ"[cubeAxis])) { stage = 411; wait = 2; } break;
            case 411: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 412; wait = 2; break;
            case 412:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                io.AddInputCharacter((uint)('2' + cubeAxis));
                if (++cubeAxis < 3) stage = 410; else stage = 413; wait = 2; break;
            case 413: if (Click("asset-editor/type")) { stage = 414; wait = 2; } break;
            case 414: io.AddKeyEvent(ImGuiKey.Escape, true); stage = 415; wait = 2; break;
            case 415:
                io.AddKeyEvent(ImGuiKey.Escape, false);
                Assert(((CubeProceduralModel)game.AssetDocument!.Asset.Type).Size == new Vector3(2, 3, 4), "New type vector editing must reach actual authored Cube fields");
                io.AddMousePosEvent(650, 500); io.AddMouseWheelEvent(0, -20); stage = 416; wait = 3; break;
            case 416: if (Click("asset-editor/material/choose")) { stage = 417; wait = 2; } break;
            case 417: if (Click("material-picker/asset//EditorParity/Ground Material")) { stage = 418; wait = 2; } break;
            case 418: if (Click("material-picker/use")) { stage = 419; wait = 2; } break;
            case 419:
                io.AddMousePosEvent(650, 500); io.AddMouseWheelEvent(0, 20); stage = 420; wait = 3; break;
            case 420: game.CaptureModelTypeCheckpoint(); if (Click("asset-editor/update")) { stage = 421; wait = 2; } break;
            case 421:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } cube)
                {
                    Assert(cube.ModelSize == new Vector3(2, 3, 4) && game.RuntimeAssetSize(cube.Name) == cube.ModelSize
                        && cube.ModelFitsCamera && cube.Camera.DefaultYaw == MathUtil.PiOverFour && cube.Camera.DefaultPitch == -MathUtil.Pi / 6
                        && cube.VertexBytes > 336 && cube.MaterialUrl?.EndsWith("Ground Material") == true,
                        "Changed asset type must compile real Cube geometry/material into preview and scene");
                    var saved = AssetFileSerializer.Load<ProceduralModelAsset>(game.AssetDocument!.Path).Asset;
                    Assert(saved.Id == beforeTypeAssetId && saved.Type is CubeProceduralModel { Size: var savedSize } && savedSize == new Vector3(2, 3, 4),
                        "Changed concrete type and its parameters must survive asset YAML");
                    AssertPlaneIsolation(); game.CaptureCubeUpdateCheckpoint();
                    if (Click("model-preview/edit")) { stage = 422; wait = 2; }
                }
                break;
            case 422: if (Click("asset-editor/undo")) { stage = 423; wait = 2; } break;
            case 423: if (Click("asset-editor/undo")) { stage = 424; wait = 2; } break;
            case 424: if (Click("asset-editor/undo")) { stage = 425; wait = 2; } break;
            case 425:
                Assert(ReferenceEquals(game.AssetDocument!.Asset.Type, beforeTypeChange) && EditedPlane.Size == new Vector2(10)
                    && EditedPlane.MaterialInstance.Material != null, "Undo through new shape edits must restore the complete original Plane object");
                if (Click("asset-editor/update")) { stage = 426; wait = 2; } break;
            case 426:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } restored)
                {
                    Assert(restored.ModelSize == new Vector3(10, 0, 10) && restored.VertexBytes == 336
                        && game.RuntimeAssetSize(restored.Name) == restored.ModelSize && game.AssetDocument!.Asset.Id == beforeTypeAssetId,
                        "Restoring previous type must rebuild original authored plane in preview and scene");
                    AssertPlaneIsolation(); checks.Add("Procedural type Cube selection/defaults, schema refresh, Undo/Redo, grouped new parameters, YAML identity, real compiled geometry and original Plane restoration");
                    Passed = Finished = true; Result = "PLANE ASSET UI PASS";
                }
                break;
        }
    }
}
