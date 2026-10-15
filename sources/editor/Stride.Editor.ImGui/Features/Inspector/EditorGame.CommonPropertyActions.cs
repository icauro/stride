using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCommonPropertyClipboardCommands(CommonComponentProperty property)
    {
        DrawCommonMemberPresenterCommands(property, aggregate: false);
        var owner = document!; var fields = property.Fields.ToArray();
        bool Current() => ReferenceEquals(document, owner)
            && sessionHistory?.Registry.Find(owner.Asset.Id) is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, owner)
            && fields.All(field => owner.CanCopyProperty(field) && field.Component.Entity is { } entity && owner.IsSelected(entity.Id));
        if (fields.All(field => field.IsContentReference)) DrawContentReferenceFetchActions(fields.Select(field => field.Node.Retrieve()));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, fields.Length == 1 && Current()))
            Execute(() =>
            {
                ValidatePendingPropertyStrings();
                if (!Current()) throw new InvalidOperationException("The selected property changed before copying.");
                Gui.SetClipboardText(owner.CopyProperty(fields[0]));
            });
        var text = Gui.GetClipboardTextS() ?? "";
        var canPaste = Current() && owner.CanPasteProperties(fields, text);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Queue(false);
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) Queue(true);
        void Queue(bool replace) => pendingCollectionAction = () =>
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The selected properties changed before pasting.");
            owner.PasteProperties(fields, text, replace);
        };
    }
    private void DrawCommonPropertyActions(CommonComponentProperty property)
    {
        if (!Gui.BeginPopupContextItem("Common property actions")) return;
        try { DrawCommonPropertyClipboardCommands(property); }
        finally { Gui.EndPopup(); }
    }
}
