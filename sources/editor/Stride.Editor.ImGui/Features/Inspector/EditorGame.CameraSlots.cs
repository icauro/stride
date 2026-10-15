using Hexa.NET.ImGui;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core.Assets;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IReadOnlyList<AssetItem>? cameraSlotAssets;
    private Package? cameraSlotPackage;
    private IReadOnlyList<CameraSlotChoice> cameraSlotChoices = [];

    private IReadOnlyList<CameraSlotChoice> CameraChoices()
    {
        var owner = workspace?.AssetItems.FirstOrDefault(item => string.Equals(item.FullPath.ToOSPath(), document?.Path, StringComparison.OrdinalIgnoreCase))?.Package;
        if (!ReferenceEquals(cameraSlotAssets, workspace?.AssetItems) || cameraSlotPackage != owner)
        {
            cameraSlotAssets = workspace?.AssetItems; cameraSlotPackage = owner;
            cameraSlotChoices = owner == null ? [] : CameraSlotChoices.Get(owner).ToArray();
        }
        return cameraSlotChoices;
    }

    private void DrawCameraSlot(ComponentField field)
    {
        Gui.PushID(field.Name); DrawDocumentedComponentLabel(field); Gui.SetNextItemWidth(-1);
        Gui.BeginDisabled(!document!.CanEditComponentValue(field) || !field.SupportsEditing);
        DrawCameraSlotValue(field.Node.Retrieve(), value => Execute(() => { document.UpdateComponent(field, value); document.EndComponentEdit(); }));
        Gui.EndDisabled(); Gui.PopID();
    }

    private void DrawCameraSlotValue(object? value, Action<SceneCameraSlotId> commit, bool mixed = false)
    {
        var current = value is SceneCameraSlotId slot ? slot : default;
        var choices = CameraChoices();
        var label = mixed ? DifferentPropertyValues : current.Id == Guid.Empty ? "None" : choices.FirstOrDefault(choice => choice.Value.Equals(current))?.Label ?? current.Id.ToString();
        if (!Gui.BeginCombo("##Slot", label)) return;
        if (Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && current.Id == Guid.Empty)) commit(default);
        foreach (var choice in choices)
        {
            Gui.PushID(choice.Value.Id.ToString()); var origin = Gui.GetCursorScreenPos();
            var clicked = Gui.Selectable("##Slot", !mixed && choice.Value.Equals(current));
            Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), choice.Label);
            if (clicked) commit(choice.Value);
            Gui.PopID();
        }
        Gui.EndCombo();
    }
}
