using System.Globalization;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawInheritedNumericDraft(ComponentField field)
    {
        var owner = document!;
        bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating
            && owner.CanOverrideInheritedComponent(field) && field.Component.Entity is { } entity && owner.IsSelected(entity.Id);
        var value = field.Node.Retrieve();
        var range = field.EffectiveNumericRange;
        var ranged = NativeRangedNumericControl.Matches(field.DeclaredType, range);
        object? display = value; var sliderActive = false; var sliderCommit = false;
        if (ranged)
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            sliderCommit = NativeRangedNumericControl.Draw(field.Node, field.Type, value, false, [range], UiScale(1), out display, out _);
            sliderActive = Gui.IsItemActive();
        }
        var feedback = Gui.GetID("##Value");
        Gui.SetNextItemWidth(-1);
        string Format(object? number) => Convert.ToString(number, CultureInfo.InvariantCulture) ?? "";
        try
        {
            PropertyGrid.Strings.Draw(field.Node, false, Editable, () => [field.Node.Retrieve()], text =>
            {
                var next = PropertyConversionFeedback.Convert(feedback, () => ParseComponentNumber(field, text));
                owner.EditInheritedScalar(field, next); componentErrors.Remove(field);
            }, () => AcquirePropertyStringOwners([owner]), text =>
            {
                if (field.Type == typeof(decimal) && !ranged) return null;
                return NativeNumericInput.StepText(field.Type, field.Node.Retrieve(), false, range, ref text) ? text : null;
            }, Format, externalText: ranged && display != null && !Equals(display, value) ? Format(display) : null,
                externalActive: sliderActive, externalCommit: sliderCommit,
                onCancelled: error => ClearCancelledPropertyDraft(error, feedback, field));
        }
        catch (Exception error)
        {
            PropertyConversionFeedback.Fail(feedback, error); componentErrors[field] = error.Message; SetPropertyDraftDiagnostic(error);
        }
        PropertyConversionFeedback.Draw(feedback); DrawInheritedResetAction(field);
    }
}
