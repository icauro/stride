using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageGuidValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var feedback = Gui.GetID("##Guid");
        Gui.SetNextItemWidth(-1);
        bool Input(string control, string hint, ref string text, ImGuiInputTextFlags flags)
            => NativeGuidInput.DrawDraft(owner, control, hint, ref text, flags);
        try
        {
            DrawPackagePropertyString(package, accessor, "##Guid", presenter, editable, Read,
                text =>
                {
                    var next = PropertyConversionFeedback.Convert(feedback, () => ParseRetainedGuid(text));
                    if (!Equals(Read(), next)) set(next);
                }, format: value => value?.ToString() ?? "", input: Input, reportErrors: false);
        }
        catch (GuidDraftConversionException error) when (packagePropertyStrings.IsPending(owner, feedback))
        {
            // Only direct native input rejection restores authored text. Explicit
            // Save/command validation throws before this renderer and keeps pins.
            PropertyConversionFeedback.Fail(feedback, error);
            statusMessage = error.Message;
            Execute(() => packagePropertyStrings.Abandon(owner, feedback));
        }
        catch (Exception error)
        {
            PropertyConversionFeedback.Fail(feedback, error);
            Execute(() => throw error);
        }
        PropertyConversionFeedback.Draw(feedback);
    }
}
