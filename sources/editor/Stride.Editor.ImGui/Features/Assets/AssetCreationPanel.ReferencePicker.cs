using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel
{
    internal Action<Choice, AssetId?, Func<bool>, Action<AssetId?>>? RequestReferencePicker { get; set; }
    internal Action<IDisposable>? RetireReferenceLease { get; set; }
    private IDisposable? templateReferenceLease;
    internal void RetainTemplateReference(IDisposable? lease)
    {
        var previous = templateReferenceLease; templateReferenceLease = lease;
        if (previous != null) { if (RetireReferenceLease != null) RetireReferenceLease(previous); else previous.Dispose(); }
    }
    private bool referencePickerPending;
    private void BrowseTemplateReference(Choice choice)
    {
        if (RequestReferencePicker == null || create == null || referencePickerPending) return;
        var command = create;
        var epoch = templateRequestEpoch;
        bool Current() => ReferenceEquals(create, command) && ReferenceEquals(selected, choice) && templateRequestEpoch == epoch;
        referencePickerPending = true;
        try
        {
            RequestReferencePicker(choice, reference, Current, picked =>
            {
                if (!Current()) return;
                referencePickerPending = false; reference = picked; requestOpen = true; focusTemplateParameters = true;
            });
            Gui.CloseCurrentPopup();
        }
        catch { referencePickerPending = false; throw; }
    }
    private long templateRequestEpoch;
}
