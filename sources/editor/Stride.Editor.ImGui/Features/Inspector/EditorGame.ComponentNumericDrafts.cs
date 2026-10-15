using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentNumeric(ComponentField field)
    {
        var owner = document!;
        Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating && owner.CanEditComponentValue(field);
                var value = field.Node.Retrieve();
                var ranged = NativeRangedNumericControl.Matches(field.DeclaredType, field.EffectiveNumericRange);
                object? display = value; var sliderCommit = false; var sliderActive = false;
                if (ranged)
                {
                    using var disabled = ImGuiScopes.Disabled(!Editable());
                    sliderCommit = NativeRangedNumericControl.Draw(field.Node, field.Type, value, false,
                        [field.EffectiveNumericRange], UiScale(1), out display, out _);
                    sliderActive = Gui.IsItemActive();
                }
                var control = (field.IsWideInteger || field.Type == typeof(decimal)) && !ranged ? "##Integer" : "##Value";
                var feedback = Gui.GetID(control);
                string Format(object? number) => Convert.ToString(number, CultureInfo.InvariantCulture) ?? "";
                try
                {
                    PropertyGrid.Strings.Draw(field.Node, false, Editable, () => [field.Node.Retrieve()], text =>
                    {
                        var next = PropertyConversionFeedback.Convert(feedback, () => ParseComponentNumber(field, text));
                        if (!Equals(field.Node.Retrieve(), next)) owner.UpdateComponent(field, next);
                        owner.EndComponentEdit(); componentErrors.Remove(field);
                    }, () => AcquirePropertyStringOwners([owner]), text =>
                    {
                        // Plain decimal has no native NumericTextBox keyboard template.
                        if (field.Type == typeof(decimal) && !ranged) return null;
                        return NativeNumericInput.StepText(field.Type, field.Node.Retrieve(), false,
                            field.EffectiveNumericRange, ref text) ? text : null;
                    }, Format, controlLabel: control,
                        externalText: ranged && display != null && !Equals(display, value) ? Format(display) : null,
                        externalActive: sliderActive, externalCommit: sliderCommit,
                        onCancelled: error => ClearCancelledPropertyDraft(error, feedback, field));
                }
                catch (Exception error)
                {
                    PropertyConversionFeedback.Fail(feedback, error); componentErrors[field] = error.Message; SetPropertyDraftDiagnostic(error);
                }
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
                PropertyConversionFeedback.Draw(feedback);
                if (componentErrors.TryGetValue(field, out var errorText)) DrawSelectableText("##NumericError", errorText);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void SetPropertyDraftDiagnostic(Exception error, bool procedural = false)
    {
        statusMessage = error.Message; statusDiagnosticOwner = error;
        if (procedural) { assetEditError = error.Message; assetEditDiagnosticOwner = error; }
    }
    private void ClearCancelledPropertyDraft(Exception? error, uint? feedback = null, ComponentField? field = null, bool procedural = false)
    {
        if (feedback is { } id) PropertyConversionFeedback.Validate(id);
        if (error == null) return;
        if (field != null && componentErrors.TryGetValue(field, out var current) && current == error.Message) componentErrors.Remove(field);
        if (ReferenceEquals(statusDiagnosticOwner, error)) statusMessage = "";
        if (procedural && ReferenceEquals(assetEditDiagnosticOwner, error)) assetEditError = "";
    }
    private static object ParseComponentNumber(ComponentField field, string text)
        => field.Type == typeof(decimal) && field.EffectiveNumericRange == null
            ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
            : NativeNumericText.Parse(text, field.Type, field.EffectiveNumericRange);
}
