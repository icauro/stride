using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private bool pickerImportBoundsVisible, pickerTransferClose;
    private Vector2 pickerImportMin, pickerImportMax;
    private readonly List<ContentReferenceEditCertificate> retiredPickerTransferCertificates = [];

    private void RecordPickerImportBounds()
    {
        pickerImportMin = Gui.GetWindowPos(); pickerImportMax = pickerImportMin + Gui.GetWindowSize();
        pickerImportBoundsVisible = true;
    }
    internal bool TryHandleNativeFileDrop(string[] files, Vector2 position)
    {
        if (modal == null) return false;
        // A live modal owns every native file drop, including a rejected drop.
        if (!modal.SourceImportAllowed || !pickerImportBoundsVisible || contextSuspended || modal.ContextMutationPending || !modal.IsCurrent()
            || position.X < pickerImportMin.X || position.Y < pickerImportMin.Y || position.X >= pickerImportMax.X || position.Y >= pickerImportMax.Y
            || modalBrowser.CaptureNavigationScope() is not { EntireWorkspace: false, Folders.Length: 1 }
            || modalBrowser.SelectedFolder is not { } folder) return true;
        var request = modal;
        var retention = request.Certificate.Retain();
        request.ContextMutationPending = true; contextSuspended = true; contextWaitFrames = 2; pickerTransferClose = true;
        host.RequestPickerSourceImport(files.ToArray(), folder, modalBrowser, retention, request.IsCurrent,
            succeeded => CompletePickerTransfer(request, succeeded));
        return true;
    }
    private void RunPickerTransfer(ModalRequest request, PickerAuthoringCommand command)
    {
        if (request.ContextMutationPending || !request.IsCurrent()) return;
        var retention = request.Certificate.Retain();
        request.ContextMutationPending = true;
        var completed = false;
        try
        {
            command(succeeded =>
            {
                if (completed) return; completed = true;
                try { CompletePickerTransfer(request, succeeded); }
                finally { RetirePickerTransferCertificate(retention); }
            });
        }
        catch
        {
            if (!completed) { completed = true; request.ContextMutationPending = false; RetirePickerTransferCertificate(retention); }
            throw;
        }
    }
    private void RetirePickerTransferCertificate(ContentReferenceEditCertificate certificate)
    {
        try { certificate.Dispose(); }
        catch { retiredPickerTransferCertificates.Add(certificate); throw; }
    }
    private void DrainPickerTransferCertificates()
    {
        foreach (var certificate in retiredPickerTransferCertificates.ToArray())
        { certificate.Dispose(); retiredPickerTransferCertificates.Remove(certificate); }
    }
    private void CompletePickerTransfer(ModalRequest request, bool succeeded)
    {
        request.ContextMutationPending = false;
        if (!ReferenceEquals(modal, request)) return;
        if (succeeded)
        {
            if (!request.Certificate.TryRebaselineAfterAuthorizedMutation()) { CancelModal(); return; }
            modalBrowser.RefreshEntries(request.Workspace);
        }
        else if (!request.IsCurrent()) CancelModal();
    }
    private bool ClosePickerTransferPopup()
    {
        if (!pickerTransferClose) return false;
        pickerTransferClose = false;
        using var popup = ImGuiScopes.Modal(ModalTitle, ImGuiWindowFlags.NoSavedSettings);
        if (popup.DrawContents) Gui.CloseCurrentPopup();
        return true;
    }
}
