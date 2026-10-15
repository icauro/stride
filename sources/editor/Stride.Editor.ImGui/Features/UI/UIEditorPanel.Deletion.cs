using Hexa.NET.ImGui;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed record UIElementDeletion(UIDocument Source, UIHierarchyDragOwner Owner, long Revision, long Layout,
        Guid[] Selection, UIElement[] Roots, UIElement?[] Parents) : IDisposable
    {
        internal string ConfirmationMessage { get; } = Roots.Length == 1
            ? EditorLocalization.Text("Are you sure you want to delete this UI element?", "Message")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                EditorLocalization.Text("Are you sure you want to delete these {0} UI elements?", "Message"), Roots.Length);
        internal bool MatchesGraph() => Source.EditRevision == Revision && Source.LayoutVersion == Layout
            && Roots.Select((element, index) => ReferenceEquals(Source.Find(element.Id), element)
                && ReferenceEquals(element.VisualParent, Parents[index])).All(value => value)
            && Source.CommonRoots(Selection).SequenceEqual(Roots.Select(element => element.Id));
        public void Dispose() => Owner.Dispose();
    }
    private UIElementDeletion? uiElementDeletion;
    private bool openUIElementDeletion, dontAskUIElementDeletion;
    private string? uiElementDeletionError;

    private void BeginUIElementDeletion(UIDocument source, Guid[] ids)
    {
        if (!host.AskBeforeDeletingUIElements)
        {
            source.Delete(ids); SelectUIResult([]); return;
        }
        var owner = host.AcquireHierarchyCommandOwner(source, true);
        if (owner == null) return;
        try
        {
            var roots = source.CommonRoots(ids).Select(source.Find).OfType<UIElement>().ToArray();
            if (roots.Length == 0) { owner.Dispose(); return; }
            var request = new UIElementDeletion(source, owner, source.EditRevision, source.LayoutVersion,
                ids.ToArray(), roots, roots.Select(element => element.VisualParent).ToArray());
            if (!owner.IsCurrent || !request.MatchesGraph()) { request.Dispose(); return; }
            CancelUIElementDeletion(); uiElementDeletion = request;
            dontAskUIElementDeletion = false; openUIElementDeletion = true;
        }
        catch { owner.Dispose(); throw; }
    }

    private void DrawUIElementDeletion()
    {
        var title = host.UIElementDeletionTitle;
        if (openUIElementDeletion) { Gui.OpenPopup(title); openUIElementDeletion = false; }
        if (!Gui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        try
        {
            if (uiElementDeletion is not { } request || !ReferenceEquals(document, request.Source)
                || !request.Owner.IsCurrent || !host.CanEditHierarchyOwner(request.Source)
                || !request.MatchesGraph() || !selected.SequenceEqual(request.Selection))
            { CancelUIElementDeletion(); Gui.CloseCurrentPopup(); return; }
            host.DrawSelectableText("ui.delete.message", request.ConfirmationMessage);
            host.DrawConfirmationCheckbox(EditorLocalization.Widget("Don't ask again"), ref dontAskUIElementDeletion);
            if (uiElementDeletionError != null) host.DrawSelectableText("ui.delete.error", uiElementDeletionError);
            if (host.DrawConfirmationButton(EditorLocalization.Widget("Delete", "Button")))
            {
                try
                {
                    host.ValidateHierarchyEdits(); request.Source.CommitEdits();
                    if (!request.Owner.IsCurrent || !request.MatchesGraph() || !selected.SequenceEqual(request.Selection))
                        throw new InvalidOperationException("The UI elements changed during deletion confirmation.");
                    if (dontAskUIElementDeletion) host.DisableUIElementDeletionConfirmation();
                    if (!QueueUICommand(request.Source, request.Selection, true, (source, ids) =>
                    {
                        if (!request.MatchesGraph()) throw new InvalidOperationException("The UI elements changed during deletion confirmation.");
                        source.Delete(ids); SelectUIResult([]);
                    })) throw new InvalidOperationException("The UI deletion owner is no longer current.");
                    CancelUIElementDeletion(); Gui.CloseCurrentPopup();
                }
                catch (Exception error) { uiElementDeletionError = error.Message; host.ReportValidationFailure(error); }
            }
            Gui.SameLine();
            var cancel = host.DrawConfirmationButton(EditorLocalization.Widget("Cancel", "Button"));
            Gui.SetItemDefaultFocus();
            if (cancel || Gui.IsKeyPressed(ImGuiKey.Escape, false))
            { CancelUIElementDeletion(); Gui.CloseCurrentPopup(); }
        }
        finally { Gui.EndPopup(); }
    }

    private void CancelUIElementDeletion()
    {
        var request = uiElementDeletion; uiElementDeletion = null;
        request?.Dispose(); openUIElementDeletion = false; uiElementDeletionError = null;
    }
}
