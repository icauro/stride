using Hexa.NET.ImGui;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal MaterialWorkflowState InspectMaterialWorkflow()
    {
        var entry = workspace?.Assets.SingleOrDefault(asset => asset.Name == "Sphere Material" && asset.AssetType == typeof(MaterialAsset));
        var active = documents.Active;
        var metadata = active?.Document as AssetMetadataDocument;
        var material = metadata?.AuthoredAsset as MaterialAsset;
        return new(entry?.Id.ToString(), entry?.Path, material?.Id.ToString(), active?.Path,
            MaterialWorkflowValue(material), metadata?.IsDirty ?? false, metadata?.CanUndo ?? false,
            metadata?.CanRedo ?? false, materialPreview?.RenderError?.ToString());
    }
    internal static float? MaterialWorkflowValue(MaterialAsset? material)
        => (material?.Attributes.MicroSurface as MaterialGlossinessMapFeature)?.GlossinessMap is ComputeFloat value ? value.Value : null;
}

internal sealed record MaterialWorkflowState(string? ExpectedId, string? ExpectedPath, string? ActiveAssetId,
    string? ActivePath, float? Glossiness, bool Dirty, bool CanUndo, bool CanRedo, string? PreviewError);

internal sealed partial class UiWorkflowTest
{
    private float originalMaterialGlossiness;
    private const string MaterialGlossinessControl = "material-settings/Attributes.MicroSurface.GlossinessMap.Value";

    private void UpdateMaterialEditorScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 10900:
                Assert(game.InspectMaterialWorkflow().ExpectedId != null, "U06 fixture requires actual Sphere Material asset");
                if (Click("asset/filter")) Next(); break;
            case 10901: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10902:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "Sphere Material") io.AddInputCharacter(character);
                Next(); break;
            case 10903: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10904: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10905: if (Click("asset/Sphere Material")) Next(); break;
            case 10906:
                var opened = game.InspectMaterialWorkflow();
                if (opened.ActiveAssetId != opened.ExpectedId || !SameAssetPath(opened.ActivePath, opened.ExpectedPath)) break;
                Assert(opened.Glossiness.HasValue && !opened.Dirty, "Material editor must inspect the real clean glossiness value");
                originalMaterialGlossiness = opened.Glossiness!.Value;
                Assert(Math.Abs(originalMaterialGlossiness - 0.375f) > 0.0001f, "Material fixture must differ from requested edit");
                if (!game.Controls.Centers.ContainsKey(MaterialGlossinessControl))
                { TryClickToolTab("editor.properties"); break; }
                if (Click(MaterialGlossinessControl)) Next();
                break;
            case 10907: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10908:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "0.375") io.AddInputCharacter(character);
                Next(); break;
            case 10909: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10910: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10911:
                if (Click("toolbar/undo")) Next(); break;
            case 10912:
                var undone = game.InspectMaterialWorkflow();
                Assert(NearMaterial(undone.Glossiness, originalMaterialGlossiness) && !undone.Dirty && undone.CanRedo,
                    "Undo must restore actual material value and clean save point");
                if (Click("toolbar/redo")) Next(); break;
            case 10913:
                var redone = game.InspectMaterialWorkflow();
                Assert(NearMaterial(redone.Glossiness, 0.375f) && redone.Dirty && redone.CanUndo && !redone.CanRedo,
                    "Redo must restore the widget-authored material change");
                checks.Add("Material numeric widget creates a real undoable authored change");
                if (Click("toolbar/save")) Next(); break;
            case 10914:
                var saved = game.InspectMaterialWorkflow();
                if (saved.Dirty) break;
                Assert(NearMaterial(saved.Glossiness, 0.375f) && saved.ActiveAssetId == saved.ExpectedId,
                    "Save must retain canonical material identity and the edited value");
                var disk = AssetFileSerializer.Load<MaterialAsset>(saved.ExpectedPath!).Asset;
                Assert(disk.Id.ToString() == saved.ExpectedId && NearMaterial(EditorGame.MaterialWorkflowValue(disk), 0.375f),
                    "Independent file deserialization must retain material identity and edited glossiness");
                checks.Add("Material Save persists canonical value and identity independently");
                CompleteIndependentSubset(); break;
        }
    }

    private static bool NearMaterial(float? value, float expected) => value.HasValue && Math.Abs(value.Value - expected) < 0.0001f;
}
