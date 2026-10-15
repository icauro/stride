using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? captureCheckpointSuffix;
    internal void CaptureSceneCheckpoint() => captureCheckpointSuffix = ".scene.png";
    internal void CaptureLoadingCheckpoint() => captureCheckpointSuffix = ".loading.png";
    internal void CaptureCloseCheckpoint() => captureCheckpointSuffix = ".close.png";
    internal void CaptureRenameCheckpoint() => captureCheckpointSuffix = ".rename.png";
    internal void CaptureImportedCheckpoint() => captureCheckpointSuffix = ".imported.png";
    internal void CaptureComponentCheckpoint() => captureCheckpointSuffix = ".components.png";
    internal void CaptureColorCheckpoint() => captureCheckpointSuffix = ".color.png";
    internal void CaptureShadowCheckpoint() => captureCheckpointSuffix = ".shadow.png";
    internal void CaptureModelPreviewCheckpoint() => captureCheckpointSuffix = ".model-preview.png";
    internal void CaptureProceduralPreviewCheckpoint(bool plane) => captureCheckpointSuffix = plane ? ".plane-preview.png" : ".sphere-preview.png";
    internal void CapturePreviewVisibilityCheckpoint() => captureCheckpointSuffix = ".preview-visibility.png";
    internal void CaptureVectorCheckpoint(int round) => captureCheckpointSuffix = round == 2 ? ".vector4.png" : round == 3 ? ".integer-vectors.png" : ".vectors.png";
    internal void CaptureNumericRangeCheckpoint() => captureCheckpointSuffix = ".numeric-range.png";
    internal void CaptureAssetEditorCheckpoint() => captureCheckpointSuffix = ".asset-editor.png";
    internal void CaptureUpdatedAssetCheckpoint() => captureCheckpointSuffix = ".asset-updated.png";
    internal void CapturePlaneEditorCheckpoint() => captureCheckpointSuffix = ".plane-editor.png";
    internal void CapturePlaneUpdateCheckpoint() => captureCheckpointSuffix = ".plane-updated.png";
    internal void CaptureMaterialPickerCheckpoint() => captureCheckpointSuffix = ".material-picker.png";
    internal void CaptureMaterialEditorCheckpoint() => captureCheckpointSuffix = ".material-editor.png";
    internal void CaptureModelTypeCheckpoint() => captureCheckpointSuffix = ".model-type.png";
    internal void CaptureCubeUpdateCheckpoint() => captureCheckpointSuffix = ".cube-updated.png";
    internal void CapturePickingCheckpoint() => captureCheckpointSuffix = ".scene-picking.png";
    internal void CaptureSelectedCubeCheckpoint() => captureCheckpointSuffix = ".selected-cube.png";
    internal void CaptureOrthographicCheckpoint() => captureCheckpointSuffix = ".orthographic.png";
    internal void CaptureTopViewCheckpoint() => captureCheckpointSuffix = ".top-view.png";
    internal void CaptureLensCheckpoint() => captureCheckpointSuffix = ".camera-lens.png";
    internal void CaptureHierarchyCheckpoint() => captureCheckpointSuffix = ".hierarchy-create.png";
    internal void CaptureHierarchyMenuCheckpoint() => captureCheckpointSuffix = ".hierarchy-create-menu.png";
    internal bool HasRuntimeEntity(Guid id, Guid? parent) => InspectionViewport?.HasRuntimeEntity(id, parent) == true;
    internal (int Passes, double Milliseconds) HierarchySynchronization => InspectionViewport is { } viewport
        ? (viewport.StructuralSynchronizationPasses, viewport.StructuralSynchronizationMilliseconds) : (0, 0);
}
