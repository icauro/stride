using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawDocumentPanel(EngineViewport viewport) => DrawDocumentGroups();

    private void DrawDocumentCloseDialog()
    {
        if (openDocumentClose) { Gui.OpenPopup("Close document"); openDocumentClose = false; }
        EditorDocumentEntry? entry;
        var choice = 0;
        using (var modal = ImGuiScopes.Modal("Close document", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!modal.DrawContents) return;
            entry = closingDocument;
            if (entry == null) { Gui.CloseCurrentPopup(); return; }
            if (!ValidateDocumentCloseTarget(entry, closingDocumentOwner))
            {
                CancelDocumentCloseBatch(clearCurrent: true);
                closingDocument = null; closingDocumentOwner = null; documentCloseError = null;
                Gui.CloseCurrentPopup(); return;
            }
            DrawSelectableText("##DocumentCloseMessage", "Save changes to " + entry.Name + " before closing?");
            DrawSelectableText("##DocumentClosePath", entry.Path);
            if (documentCloseError != null) DrawSelectableText("##DocumentCloseError", "Save failed: " + documentCloseError);
            Gui.BeginDisabled(!CanPumpDocumentCloseRequests);
            if (DrawNativeButton(EditorLocalization.Widget("Save and close", "Button"))) choice = 1;
            Controls.Record("document-close/save");
            Gui.SameLine();
            if (DrawNativeButton("Discard and close")) choice = 2;
            Controls.Record("document-close/discard");
            Gui.EndDisabled();
            Gui.SameLine();
            if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"))) choice = 3;
            Controls.Record("document-close/cancel");
            if (choice != 0) Gui.CloseCurrentPopup();
        }
        if (choice == 0 || !ReferenceEquals(closingDocument, entry) || !documents.Entries.Contains(entry)) return;
        if (choice == 3)
        {
            CancelDocumentCloseBatch(clearCurrent: true);
            selectDocumentTab = documents.Active;
            closingDocument = null; closingDocumentOwner = null; documentCloseError = null;
            return;
        }
        try
        {
            if (!ValidateDocumentCloseTarget(entry, closingDocumentOwner))
                throw new InvalidOperationException("The document close owner changed.");
            if (!CanPumpDocumentCloseRequests)
                throw new InvalidOperationException("The document close must wait for the current authoring operation.");
            if (choice == 1)
            {
                ValidatePendingPropertyStrings();
                if (!ReferenceEquals(closingDocument, entry) || !ValidateDocumentCloseTarget(entry, closingDocumentOwner))
                    throw new InvalidOperationException("The document changed while finishing pending edits.");
                entry.Document.Save();
                SynchronizeSavedGameSettingsOrientations();
            }
            else
            {
                DiscardDocumentChanges(entry);
                AcceptDocumentCloseDiscard(entry);
            }
            if (!ReferenceEquals(closingDocument, entry) || !ValidateDocumentCloseTarget(entry, closingDocumentOwner))
                throw new InvalidOperationException("The document changed before closing.");
            CloseDocument(entry);
            closingDocument = null; closingDocumentOwner = null;
        }
        catch (Exception error)
        {
            documentCloseError = statusMessage = error.Message;
            if (ReferenceEquals(closingDocument, entry) && ValidateDocumentCloseTarget(entry, closingDocumentOwner)) openDocumentClose = true;
            else
            {
                closingDocument = null; closingDocumentOwner = null;
                if (documents.Entries.Contains(entry)) CancelDocumentCloseBatch(clearCurrent: true);
            }
        }
    }
}
