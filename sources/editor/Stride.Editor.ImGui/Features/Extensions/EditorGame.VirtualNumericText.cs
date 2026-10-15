using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeVirtualNumber(IEditorDocument owner, VirtualNodePresenter presenter, Type type, object? value,
        bool mixed, PropertyNumericRange? range, IReadOnlyList<PropertyNumericRange?>? ranges,
        Func<bool> canEdit, Func<object?> read, Action<object?> assign, Action end,
        Func<object?[]>? readValues, Func<IDisposable>? acquire)
    {
        bool Editable() => !IsAuthoringPipelineUpdating && canEdit();
        var effective = ranges != null ? NativeNumericInput.Combine(type, ranges) : range;
        var ranged = NativeRangedNumericControl.Matches(presenter.Type, range);
        object? display = value; var sliderCommit = false; var sliderActive = false;
        if (ranged)
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            sliderCommit = NativeRangedNumericControl.Draw(presenter, presenter.Type, value, mixed,
                ranges ?? [range], UiScale(1), out display, out _);
            sliderActive = Gui.IsItemActive();
        }
        var feedback = Gui.GetID("##Value");
        string Format(object? number) => number == null ? "" : NativeNumericText.Format(number, type, effective);
        try
        {
            PropertyGrid.Strings.Draw(presenter, mixed, Editable, () => readValues?.Invoke() ?? [read()], text =>
            {
                var replacement = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, effective));
                assign(replacement); end();
            }, acquire ?? (() => AcquireRetainedVirtualColor(owner, presenter)),
                step: text => type == typeof(decimal) && !ranged ? null
                    : NativeNumericInput.StepText(type, read(), mixed, effective, ref text) ? text : null,
                format: Format, externalText: ranged && display != null && !Equals(display, value) ? Format(display) : null,
                externalActive: sliderActive, externalCommit: sliderCommit);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}