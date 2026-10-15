using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private long backgroundPickerSelectionVersion;
    private WeakReference<ModalRequest>? backgroundPickerRequest;
    private void DrawPickerBackgroundContext(ModalRequest request)
    {
        var disabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0;
        if (!Gui.BeginPopupContextWindow("##PickerBackgroundActions", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) return;
        try
        {
            if (Gui.IsWindowAppearing())
            { backgroundPickerSelectionVersion = modalBrowser.SelectionVersion; backgroundPickerRequest = new(request); }
            if (backgroundPickerRequest == null || !backgroundPickerRequest.TryGetTarget(out var captured) || !ReferenceEquals(captured, request)
                || !ReferenceEquals(modal, request) || !request.IsCurrent() || request.ContextMutationPending
                || modalBrowser.SelectionVersion != backgroundPickerSelectionVersion || disabled || Gui.GetIO().AppFocusLost)
            { Gui.CloseCurrentPopup(); return; }
            DrawPickerContextItems(modalBrowser.SelectedContent.LastOrDefault(), request);
        }
        finally { Gui.EndPopup(); }
    }
}
