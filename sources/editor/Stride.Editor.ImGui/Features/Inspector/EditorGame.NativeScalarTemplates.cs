using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawNativeScalarTemplate(object owner, Type type, object? value, bool mixed, bool canEdit, Action<object?> commit,
        Action<string>? capture = null)
    {
        if (type == typeof(DateTime))
        {
            if (NativeDateTimeInput.Draw(icons!, owner, value is DateTime date ? date : default, mixed, canEdit, UiScale(1), out var updated)) commit(updated);
            return true;
        }
        if (type == typeof(TimeSpan))
        {
            if (NativeTimeSpanInput.Draw(owner, value is TimeSpan duration ? duration : default, mixed, canEdit, out var updated, out _)) commit(updated);
            return true;
        }
        if (type == typeof(Guid))
        {
            if (NativeGuidInput.Draw(owner, value is Guid guid ? guid : default, mixed, canEdit, out var updated)) commit(updated);
            return true;
        }
        if (type == typeof(char))
        {
            if (NativeCharacterInput.Draw(owner, value is char character ? character : default, mixed, canEdit, UiScale(1), out var updated, capture)) commit(updated);
            return true;
        }
        if (type == typeof(AngleSingle))
        {
            if (NativeAngleInput.Draw(owner, value is AngleSingle angle ? angle : default, mixed, canEdit, out var updated)) commit(updated);
            return true;
        }
        return false;
    }
    private bool DrawComponentNativeScalar(ComponentField field)
    {
        if (field.IsNavigationGroup) return false;
        if (IsTemporalType(field.Type)) { DrawComponentRetainedTemporal(field); return true; }
        if (field.Type == typeof(char)) { DrawComponentRetainedCharacter(field); return true; }
        if (field.Type == typeof(Guid)) { DrawComponentRetainedGuid(field); return true; }
        if (field.Type == typeof(AngleSingle)) { DrawComponentRetainedAngle(field); return true; }
        if (field.Type != typeof(char) && field.Type != typeof(AngleSingle) && field.Type != typeof(Guid) && field.Type != typeof(TimeSpan) && field.Type != typeof(DateTime)) return false;
        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return true; }
        DrawNativeScalarTemplate(field.Node, field.Type, field.Node.Retrieve(), false, document!.CanEditComponentValue(field) && field.SupportsEditing,
            updated =>
            {
                try { document.UpdateComponent(field, updated); document.EndComponentEdit(); componentErrors.Remove(field); }
                catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
            }, Controls.Enabled ? part => Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/" + part) : null);
        if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
        Gui.EndTable(); Gui.PopID(); return true;
    }
}
