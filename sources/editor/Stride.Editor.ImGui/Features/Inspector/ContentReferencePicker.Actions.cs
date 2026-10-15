namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private void DrawReferenceActions(Type type, object? value, bool allowsNull, bool canEdit, bool mixed,
        bool forceAssignment, Action<object?> commit)
    {
        if (host.DrawReferenceMenuItem(EditorLocalization.Text("Select the referenced asset"), !mixed && host.CanFetch(value)))
            host.Fetch(value);
        if (host.DrawReferenceMenuItem(EditorLocalization.Text("Select an asset"), canEdit))
            RequestModal(type, value, commit, mixed, forceAssignment);
        if (host.DrawReferenceMenuItem(EditorLocalization.Text("Clear this reference"), canEdit && allowsNull && (mixed || value != null)))
        {
            var isCurrent = host.CaptureReferenceEditGuard();
            host.Assign(type, null, replacement =>
            {
                if (!isCurrent()) throw new InvalidOperationException("The reference property is no longer current.");
                commit(replacement);
            });
        }
    }
}
