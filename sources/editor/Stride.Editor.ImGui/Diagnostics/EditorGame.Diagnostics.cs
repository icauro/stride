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
    internal readonly UiControls Controls = new();
    internal bool DiagnosticRenderHiddenScenes => args.Contains("--ui-test") && args.Contains("--render-hidden-scenes");
    internal readonly SceneDrawMeasurements SceneDrawMeasurements = new();
    internal object MeasurementDataset => new { entities = document?.Asset.Hierarchy.Parts.Count, assets = workspace?.Assets.Count,
        models = InspectionViewport?.ModelCount, sceneTargetWidth = InspectionViewport?.Target?.Width, sceneTargetHeight = InspectionViewport?.Target?.Height };
    internal int EngineDrawCalls => engineDrawCalls;
    internal bool UsesCompiledProjectContent => InspectionViewport is { UsesAuthoredCompositor: true, BackgroundCount: > 0 };
    internal bool RuntimeComponentMatches(ComponentField entry) => InspectionViewport?.MatchesComponent(entry) == true;
    internal ImportedModelEvidence ImportedModelEvidence => document != null && workspace != null && InspectionViewport is { } viewport
        ? viewport.InspectImportedFixture(document, workspace) : new(false, false, 0, 0, 0, 0, null);
    internal bool SelectedEngineTransformMatches => document?.Selected != null && InspectionViewport?.MatchesTransform(document.Selected) == true;
    internal string EngineDiagnostic => $"models={InspectionViewport?.ModelCount}, calls={engineDrawCalls}, visible={InspectionViewport?.Visible}, gaps={string.Join(';', InspectionViewport?.Gaps ?? [])}";
}
