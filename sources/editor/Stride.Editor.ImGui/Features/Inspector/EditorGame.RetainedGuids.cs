using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class GuidDraftConversionException(FormatException error) : FormatException(error.Message, error) { }
    private static Guid ParseRetainedGuid(string text)
    {
        try { return Guid.Parse(text); }
        catch (FormatException error) { throw new GuidDraftConversionException(error); }
    }
    private void DrawRetainedGuid(object identity, Func<object?[]> read, Func<bool> canEdit, Action<Guid> assign,
        Func<IDisposable> acquire, int valueCount = 1)
    {
        var values = read(); var mixed = values.Take(valueCount).Any(value => !Equals(value, values[0]));
        var feedback = Gui.GetID("##Guid"); Gui.SetNextItemWidth(-1);
        bool Input(string control, string hint, ref string text, ImGuiInputTextFlags flags)
            => NativeGuidInput.DrawDraft(identity, control, hint, ref text, flags);
        try
        {
            PropertyGrid.Strings.Draw(identity, mixed, () => !IsAuthoringPipelineUpdating && canEdit(), read,
                text => assign(PropertyConversionFeedback.Convert(feedback, () => ParseRetainedGuid(text))), acquire,
                format: value => value?.ToString() ?? "", controlLabel: "##Guid", input: Input);
        }
        catch (GuidDraftConversionException error) when (PropertyGrid.Strings.IsPending(identity, feedback))
        {
            PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message;
            // Native GuidToString rejects invalid conversion and restores the authored source on UI validation.
            // Explicit command preflight still throws before reaching this renderer, retaining its failed draft.
            PropertyGrid.Strings.Abandon(identity, feedback);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
    private void DrawComponentRetainedGuid(ComponentField field)
    {
        var owner = document!; Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                DrawRetainedGuid(field.Node, () => [field.Node.Retrieve()],
                    () => ReferenceEquals(document, owner) && owner.CanEditComponentValue(field),
                    guid => { owner.UpdateComponent(field, guid); owner.EndComponentEdit(); componentErrors.Remove(field); },
                    () => AcquirePropertyStringOwners([owner]));
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void DrawRetainedCombinedVirtualGuid(IReadOnlyList<ComponentVirtualOwner> source)
    {
        var entries = source.ToArray(); var first = entries[0].Node;
        DrawRetainedGuid(first, () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
            () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
            guid => ApplyCombinedComponentVirtualCore(entries, (_, _) => guid),
            () => AcquireRetainedCombinedVirtualTargets(entries), entries.Length);
    }
}
