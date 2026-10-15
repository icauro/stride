using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Assets.Entities;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private int vectorRound;
    private readonly List<object> vectorWorkflowEvidence = [];
    private static readonly string[] VectorFixtureProperties = ["KeyboardMovementSpeed", "KeyboardRotationSpeed", "ShaderWeights", "PixelOffset", "GridResolution", "ClipRectangle"];
    private string VectorEntityName => vectorRound < 2 ? "Camera" : "Sphere";
    private string SecondVectorAxis => vectorRound is 2 or 5 ? "W" : "Y";
    private ComponentField CameraVectorField => game.Document!.GetComponentFields(game.Document.Selected!.Components.Single(component => component.GetType().Name == (vectorRound < 2 ? "BasicCameraController" : "InspectorTypes")))
        .Single(entry => entry.Name == VectorFixtureProperties[vectorRound]);
    private string VectorControl(string axis) => "component/" + CameraVectorField.Component.Id + "/" + CameraVectorField.Name + "/axis/" + axis;
    private object ExpectedVector(bool edited) => vectorRound switch
    {
        0 => new Vector3(edited ? 7.5f : 5f, edited ? 8.25f : 5f, 5f),
        1 => new Vector2(edited ? 7.5f : 3f, edited ? 8.25f : 3f),
        2 => new Vector4(edited ? 7.5f : 0.125f, 0.25f, 0.5f, edited ? 8.25f : 1f),
        3 => new Int2(edited ? 17000001 : 1, edited ? -17000003 : 2),
        4 => new Int3(edited ? 17000001 : 32, edited ? -17000003 : 16, 8),
        _ => new Int4(edited ? 17000001 : 0, 0, 1920, edited ? -17000003 : 1080),
    };
    private string VectorInputText(bool edited, bool second) => edited ? vectorRound >= 3 ? second ? "-17000003" : "17000001" : second ? "8.25" : "7.5"
        : vectorRound switch { 0 => "5", 1 => "3", 2 => second ? "1" : "0.125", 3 => second ? "2" : "1", 4 => second ? "16" : "32", _ => second ? "1080" : "0" };
    private void AssertCameraVector(bool edited)
    {
        var field = CameraVectorField;
        Assert(field.SupportsEditing && Equals(field.Node.Retrieve(), ExpectedVector(edited)) && game.RuntimeComponentMatches(field),
            "Vector edit must preserve sibling axes and match the real compiled project-script component");
    }
    private void UpdateVectorWorkflow()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 900: if (Click("entity/" + VectorEntityName)) { stage = 901; wait = 2; } break;
            case 901:
                var other = vectorRound < 2 ? (EntityComponent)game.Document!.Selected!.Get<CameraComponent>() : game.Document!.Selected!.Get<ModelComponent>();
                var header = "component/" + other.Id + "/header";
                if (game.Controls.Expanded.TryGetValue(header, out var expanded) && !expanded || Click(header)) { stage = 902; wait = 2; }
                break;
            case 902:
                if (Click(VectorControl("X"))) { stage = 903; wait = 2; }
                else { io.AddMousePosEvent(1150, 650); io.AddMouseWheelEvent(0, -2); wait = 2; }
                break;
            case 903: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 904; wait = 2; break;
            case 904:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in VectorInputText(true, false)) io.AddInputCharacter(character); stage = 905; wait = 2; break;
            case 905:
                if (Click(VectorControl(SecondVectorAxis))) { stage = 906; wait = 2; }
                else { io.AddMousePosEvent(1150, 650); io.AddMouseWheelEvent(0, -1); wait = 2; }
                break;
            case 906: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 907; wait = 2; break;
            case 907:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in VectorInputText(true, true)) io.AddInputCharacter(character); stage = 908; wait = 2; break;
            case 908: if (Click("entity/" + VectorEntityName)) { stage = 909; wait = 2; } break;
            case 909:
                AssertCameraVector(true); Assert(game.Document!.IsDirty, "Vector editing must dirty the authored scene");
                if (Click("toolbar/undo")) { stage = 910; wait = 2; } break;
            case 910:
                AssertCameraVector(false); Assert(!game.Document!.IsDirty, "One undo must restore both axes and the original clean save point");
                if (Click("toolbar/redo")) { stage = 911; wait = 2; } break;
            case 911: AssertCameraVector(true); if (Click("toolbar/save")) { stage = 912; wait = 2; } break;
            case 912:
                var field = CameraVectorField;
                var saved = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset.Hierarchy.Parts[field.Component.Entity.Id].Entity.Components.Single(component => component.Id == field.Component.Id);
                Assert(Equals(field.ReadRuntime(saved), ExpectedVector(true)), "Vector values and component identity must round-trip through the scene asset");
                vectorWorkflowEvidence.Add(new { entity = VectorEntityName, componentId = field.Component.Id,
                    property = field.Name, type = field.Type.Name, axesEdited = "X/" + SecondVectorAxis,
                    authored = field.Node.Retrieve()!.ToString(), persisted = field.ReadRuntime(saved)!.ToString(),
                    runtimeMatches = game.RuntimeComponentMatches(field), undoRestoredOriginal = true });
                if (vectorRound is 0 or 2 or 3) { io.AddMousePosEvent(700, 120); stage = 925; wait = 2; }
                else if (Click("toolbar/reload")) { stage = 913; wait = 2; }
                break;
            case 925: game.CaptureVectorCheckpoint(vectorRound); stage = 926; wait = 2; break;
            case 926: if (Click("toolbar/reload")) { stage = 913; wait = 2; } break;
            case 913: if (Click("entity/" + VectorEntityName)) { stage = 914; wait = 2; } break;
            case 914:
                AssertCameraVector(true); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Saved vector must reopen with clean history");
                // Restore through the same widgets; never mutate the authored fixture through test services.
                if (Click(VectorControl("X"))) { stage = 915; wait = 2; }
                else { io.AddMousePosEvent(1150, 650); io.AddMouseWheelEvent(0, -2); wait = 2; }
                break;
            case 915: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 916; wait = 2; break;
            case 916:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in VectorInputText(false, false)) io.AddInputCharacter(character); stage = 917; wait = 2; break;
            case 917:
                if (Click(VectorControl(SecondVectorAxis))) { stage = 918; wait = 2; }
                else { io.AddMousePosEvent(1150, 650); io.AddMouseWheelEvent(0, -1); wait = 2; }
                break;
            case 918: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 919; wait = 2; break;
            case 919:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in VectorInputText(false, true)) io.AddInputCharacter(character); stage = 920; wait = 2; break;
            case 920: if (Click("toolbar/save")) { stage = 921; wait = 2; } break;
            case 921: AssertCameraVector(false); if (Click("toolbar/reload")) { stage = 922; wait = 2; } break;
            case 922: if (Click("entity/" + VectorEntityName)) { stage = 923; wait = 2; } break;
            case 923:
                AssertCameraVector(false); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Restored vector must reopen cleanly");
                if (vectorRound < 5) { vectorRound++; stage = 900; wait = 2; }
                else
                {
                    checks.Add("Vector2/Vector3/Vector4 and Int2/Int3/Int4 axis editing on compiled project scripts, grouped undo/redo, exact integer values, runtime synchronization, save/reopen and fixture restoration");
                    if (Click("entity/Sphere")) { stage = 14; wait = 2; }
                }
                break;
        }
    }
}
