using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Assets.Entities;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private int rangeRound, rangePhase;
    private bool rangeStepsVerified;
    private object RangeOriginal => rangeRound switch { 0 => (object)45f, 1 => 4, 2 => 0.5d, _ => 0.5f };
    private string RangeEntity => rangeRound == 0 ? "Camera" : rangeRound == 3 ? "Directional light" : "Sphere";
    private ComponentField RangeField => SceneDocument.Flatten(game.Document!.GetComponentFields(game.Document.Selected!.Components.Single(component =>
        component.GetType().Name == (rangeRound == 0 ? "CameraComponent" : rangeRound == 3 ? "LightComponent" : "InspectorTypes"))))
        .Single(entry => entry.Name == (rangeRound == 0 ? "VerticalFieldOfView" : rangeRound == 1 ? "SampleCount" : rangeRound == 2 ? "ExposureLimit" : "Type.Shadow.PartitionMode.PSSMFactor"));
    private string RangeControl => "component/" + RangeField.Component.Id + "/" + RangeField.Name;
    private object RangeExpected => rangeRound switch
    {
        0 => (object)(rangePhase == 0 ? 179f : rangePhase == 1 ? 1f : 45f),
        1 => rangePhase == 0 ? 10 : rangePhase == 1 ? 1 : 4,
        2 => rangePhase == 0 ? 2d : rangePhase == 1 ? 0.1d : 0.5d,
        _ => rangePhase == 0 ? 1f : rangePhase == 1 ? 0f : rangePhase == 3 ? 0.04f : 0.5f,
    };
    private void AssertRangeValue(object expected) => Assert(Equals(RangeField.Node.Retrieve(), expected)
        && game.RuntimeComponentMatches(RangeField), "Ranged scalar must match normalized authored and real runtime values");

    private void UpdateNumericRangeWorkflow()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 950: if (Click("entity/" + RangeEntity)) { stage = 951; wait = 2; } break;
            case 951:
                var header = "component/" + RangeField.Component.Id + "/header";
                if (game.Controls.Expanded.TryGetValue(header, out var expanded) && expanded || Click(header)) { stage = rangeRound == 3 ? 973 : 952; wait = 2; }
                break;
            case 973: if (ExpandLightPath("Type")) { stage = 974; wait = 2; } break;
            case 974: if (ExpandLightPath("Type.Shadow")) { stage = 975; wait = 2; } break;
            case 975: if (ExpandLightPath("Type.Shadow.PartitionMode")) { stage = 952; wait = 2; } break;
            case 952:
                if (rangePhase == 0 && !rangeStepsVerified && Click(RangeControl + "/increase")) { stage = 965; wait = 2; }
                else if ((rangePhase != 0 || rangeStepsVerified) && Click(RangeControl)) { stage = 953; wait = 2; }
                else { io.AddMousePosEvent(1150, 650); io.AddMouseWheelEvent(0, -2); wait = 2; }
                break;
            case 965:
                AssertRangeValue(rangeRound switch { 0 => (object)46f, 1 => 5, 2 => 0.6d, _ => 0.51f });
                if (Click("toolbar/undo")) { stage = 966; wait = 2; } break;
            case 966:
                AssertRangeValue(RangeOriginal); Assert(!game.Document!.IsDirty, "Small-step undo must restore clean data");
                if (Click(RangeControl + "/decrease")) { stage = 977; wait = 2; } break;
            case 977:
                AssertRangeValue(rangeRound switch { 0 => (object)44f, 1 => 3, 2 => 0.4d, _ => 0.49f });
                if (Click("toolbar/undo")) { stage = 978; wait = 2; } break;
            case 978:
                AssertRangeValue(RangeOriginal); Assert(!game.Document!.IsDirty, "Decrease-step undo must restore clean data");
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); stage = 967; wait = 2; break;
            case 967: if (Click(RangeControl + "/increase")) { stage = 968; wait = 2; } break;
            case 968:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                AssertRangeValue(rangeRound switch { 0 => (object)55f, 1 => 7, 2 => 1d, _ => 0.6f });
                if (Click("toolbar/undo")) { stage = 969; wait = 2; } break;
            case 969:
                AssertRangeValue(RangeOriginal); Assert(!game.Document!.IsDirty, "Large-step undo must restore clean data");
                rangeStepsVerified = true; rangePhase = rangeRound == 3 ? 3 : 0; stage = 952; wait = 2; break;
            case 953: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 954; wait = 2; break;
            case 954:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                var text = rangePhase == 0 ? rangeRound == 1 ? "2147483648" : "200" : rangePhase == 1 ? "-5" : rangePhase == 3 ? "0.045" : rangeRound == 0 ? "45" : rangeRound == 1 ? "4" : "0.5";
                foreach (var character in text) io.AddInputCharacter(character); stage = 955; wait = 2; break;
            case 955: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 956; wait = 2; break;
            case 956: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 957; wait = 2; break;
            case 957: if (Click("entity/" + RangeEntity)) { stage = 958; wait = 2; } break;
            case 958:
                AssertRangeValue(RangeExpected); Assert(game.Document!.IsDirty, "Ranged edit must participate in document history");
                if (rangePhase == 3) { if (Click("toolbar/undo")) { stage = 976; wait = 2; } }
                else if (rangePhase == 0) { if (Click("toolbar/undo")) { stage = 959; wait = 2; } }
                else if (Click("toolbar/save")) { stage = 961; wait = 2; }
                break;
            case 976:
                AssertRangeValue(RangeOriginal); Assert(!game.Document!.IsDirty, "Decimal text precision must undo to clean source data");
                rangePhase = 0; stage = 952; wait = 2; break;
            case 959:
                AssertRangeValue(RangeOriginal);
                Assert(!game.Document!.IsDirty, "Undo must restore the original range field and save point");
                if (Click("toolbar/redo")) { stage = 960; wait = 2; } break;
            case 960: AssertRangeValue(RangeExpected); if (Click("toolbar/save")) { stage = 961; wait = 2; } break;
            case 961:
                var field = RangeField;
                var saved = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset.Hierarchy.Parts[field.Component.Entity.Id].Entity.Components.Single(component => component.Id == field.Component.Id);
                Assert(Equals(field.ReadRuntime(saved), RangeExpected), "Normalized range values must persist by component identity");
                if (rangeRound == 0 && rangePhase == 0) { io.AddMousePosEvent(700, 120); stage = 971; wait = 2; }
                else if (Click("toolbar/reload")) { stage = 963; wait = 2; }
                break;
            case 971: game.CaptureNumericRangeCheckpoint(); stage = 972; wait = 2; break;
            case 972: if (Click("toolbar/reload")) { stage = 963; wait = 2; } break;
            case 963: if (Click("entity/" + RangeEntity)) { stage = 964; wait = 2; } break;
            case 964:
                AssertRangeValue(RangeExpected); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Normalized range field must reopen cleanly");
                if (rangePhase < 2) { rangePhase++; stage = 951; wait = 2; }
                else
                {
                    checks.Add("Numeric range " + RangeField.Name + ": small/Ctrl-large steps, upper/lower input normalization, runtime/history/save/reopen and UI restoration");
                    rangePhase = 0; rangeRound++; rangeStepsVerified = false; stage = rangeRound < 4 ? 950 : 900; wait = 2;
                }
                break;
        }
    }
}
