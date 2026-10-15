using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RecordCompositorWorkflowBlock(Guid id, Vector2 point, Vector2 minimum, Vector2 maximum)
    {
        if (!Controls.Enabled) return;
        var control = "compositor/block/" + id;
        if (point.X > minimum.X && point.Y > minimum.Y && point.X < maximum.X && point.Y < maximum.Y)
            Controls.Centers[control] = point;
        else Controls.Centers.Remove(control);
    }

    internal CompositorWorkflowState InspectCompositorWorkflow()
    {
        var expected = workspace?.Assets.SingleOrDefault(asset => asset.Name == "GraphicsCompositor" && asset.AssetType == typeof(GraphicsCompositorAsset));
        var active = documents.Active;
        var document = active?.Document as AssetMetadataDocument;
        var asset = document?.CompositorAsset;
        var block = document?.CompositorBlocks.FirstOrDefault(block => block.Id != Guid.Empty && block.Value is ForwardRenderer);
        var selected = document != null && compositorEditors.TryGetValue(document, out var state) && block != null
            && state.SelectedBlocks.SetEquals([block.Id]) && state.PropertySelection?.Block == block.Id;
        return new(expected?.Id.ToString(), expected?.Path, asset?.Id.ToString(), active?.Path,
            block?.Id, (block?.Value as ForwardRenderer)?.LightProbes, selected,
            document?.IsDirty ?? false, document?.CanUndo ?? false, document?.CanRedo ?? false);
    }

    internal Vector2? InspectCompositorWorkflowPan(Guid renderer)
    {
        if (documents.Active?.Document is not AssetMetadataDocument document || !compositorEditors.TryGetValue(document, out var state)
            || !state.BoundsMin.TryGetValue(renderer, out var minimum) || !state.BoundsMax.TryGetValue(renderer, out var maximum)
            || !Controls.Centers.TryGetValue("compositor/canvas", out var center)) return null;
        var delta = center - (minimum + maximum) * .5f;
        return new(Math.Clamp(delta.X, -40, 40), Math.Clamp(delta.Y, -40, 40));
    }
}

internal sealed record CompositorWorkflowState(string? ExpectedId, string? ExpectedPath,
    string? ActiveId, string? ActivePath, Guid? RendererId, bool? LightProbes, bool Selected,
    bool Dirty, bool CanUndo, bool CanRedo);

internal sealed partial class UiWorkflowTest
{
    private Guid compositorWorkflowRenderer;
    private bool compositorWorkflowOriginalLightProbes;
    private Vector2 compositorWorkflowPanStart, compositorWorkflowPanDelta;

    private void UpdateCompositorEditorScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 11900:
                Assert(game.InspectCompositorWorkflow().ExpectedId != null, "Compositor case requires the actual fixture asset");
                if (Click("asset/filter")) Next(); break;
            case 11901: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11902:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "GraphicsCompositor") io.AddInputCharacter(character);
                Next(); break;
            case 11903: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11904: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11905: if (Click("asset/GraphicsCompositor")) Next(); break;
            case 11906:
                var opened = game.InspectCompositorWorkflow();
                if (opened.ActiveId != opened.ExpectedId || !SameAssetPath(opened.ActivePath, opened.ExpectedPath))
                { Click("asset/edit"); break; }
                Assert(opened.RendererId.HasValue && opened.LightProbes.HasValue && !opened.Dirty,
                    "Actual canonical compositor must start with a clean native shared renderer");
                compositorWorkflowRenderer = opened.RendererId.Value;
                compositorWorkflowOriginalLightProbes = opened.LightProbes.Value;
                if (game.Controls.Centers.ContainsKey("compositor/block/" + compositorWorkflowRenderer))
                { if (Click("compositor/block/" + compositorWorkflowRenderer)) Next(); }
                else if (game.Controls.Centers.TryGetValue("compositor/canvas", out var center)
                    && game.InspectCompositorWorkflowPan(compositorWorkflowRenderer) is { } delta)
                {
                    compositorWorkflowPanStart = center; compositorWorkflowPanDelta = delta;
                    io.AddMousePosEvent(center.X, center.Y); stage = 11920; wait = 2;
                }
                else TryClickToolTab("editor.document");
                break;
            case 11907:
                Assert(game.InspectCompositorWorkflow().Selected, "Native compositor canvas block click must select its real renderer for properties");
                if (ClickInspectorControl("compositor/property/.LightProbes")) Next(); break;
            case 11908:
                var edited = game.InspectCompositorWorkflow();
                Assert(edited.RendererId == compositorWorkflowRenderer && edited.LightProbes == !compositorWorkflowOriginalLightProbes
                    && edited.Dirty && edited.CanUndo, "Checkbox must commit an actual canonical renderer change and history entry");
                if (Click("toolbar/undo")) Next(); break;
            case 11909:
                var undone = game.InspectCompositorWorkflow();
                Assert(undone.RendererId == compositorWorkflowRenderer && undone.LightProbes == compositorWorkflowOriginalLightProbes
                    && !undone.Dirty && undone.CanRedo, "Undo must restore renderer value, identity and clean savepoint");
                if (Click("toolbar/redo")) Next(); break;
            case 11910:
                var redone = game.InspectCompositorWorkflow();
                Assert(redone.RendererId == compositorWorkflowRenderer && redone.LightProbes == !compositorWorkflowOriginalLightProbes
                    && redone.Dirty && redone.CanUndo && !redone.CanRedo, "Redo must restore the same widget-authored renderer change");
                if (Click("toolbar/save")) Next(); break;
            case 11911:
                var saved = game.InspectCompositorWorkflow();
                if (saved.Dirty) break;
                Assert(saved.ActiveId == saved.ExpectedId && saved.RendererId == compositorWorkflowRenderer,
                    "Save must preserve canonical compositor and renderer identities");
                var persisted = AssetFileSerializer.Load<GraphicsCompositorAsset>(saved.ExpectedPath!).Asset;
                var renderer = persisted.SharedRenderers.Single(item => item.Id == compositorWorkflowRenderer) as ForwardRenderer;
                Assert(persisted.Id.ToString() == saved.ExpectedId && renderer?.LightProbes == !compositorWorkflowOriginalLightProbes,
                    "Independent native compositor reload must retain actual renderer GUID and checkbox-authored state");
                checks.Add("Real compositor canvas selection, native renderer LightProbes checkbox, undo/redo/save and independent asset reload");
                CompleteIndependentSubset(); break;
            case 11920: io.AddMouseButtonEvent(2, true); Next(); break;
            case 11921:
                var destination = compositorWorkflowPanStart + compositorWorkflowPanDelta;
                io.AddMousePosEvent(destination.X, destination.Y); Next(); break;
            case 11922: io.AddMouseButtonEvent(2, false); Next(); break;
            case 11923: stage = 11906; wait = 2; break;
        }
    }
}
