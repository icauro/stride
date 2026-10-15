using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private void DrawReferenceText(Type type, object? value, bool allowsNull, bool canEdit, bool mixed,
        bool broken, ReferenceDraft draft, uint identity, Action<object?> commit)
    {
        var wasEditing = draft.Editing;
        var reference = ContentAssetReference.Read(value);
        void BeginEdit()
        {
            draft.SourceNull = value == null; draft.SourceMixed = mixed;
            draft.SourceId = reference?.Id; draft.SourceLocation = reference?.Location;
            draft.IsCurrent = host.CaptureReferenceEditGuard(); draft.Edited = false;
        }
        void Reset()
        {
            draft.Text = draft.Original; draft.Edited = false; draft.IsCurrent = null;
        }
        if (broken) Gui.PushStyleColor(ImGuiCol.Text, new Vector4(.95f, .32f, .32f, 1));
        bool submitted;
        // The inactive URL is painted with the native label layout. Reserve its
        // measured line height in the input frame instead of clipping to ImGui's glyph height.
        var padding = Gui.GetStyle().FramePadding;
        var nativeHeight = host.MeasurePickerLabel(draft.Text).Y;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(padding.X,
            padding.Y + Math.Max(0, nativeHeight - Gui.GetFontSize()) * .5f));
        try { submitted = NativeStringInput.Draw("##ContentReference", ref draft.Text,
            ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue
                | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly)); }
        finally { Gui.PopStyleVar(); if (broken) Gui.PopStyleColor(); }
        draft.Editing = Gui.IsItemActive();
        if (!draft.Editing && !Gui.IsItemFocused()) DrawReferenceOverlay(type, value, mixed, broken);
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Gui.SetTooltip(mixed ? EditorLocalization.Text("(Different values)") : Label(type, value, out _));
        if (canEdit && draft.IsCurrent == null && (draft.Editing || Gui.IsItemEdited())) BeginEdit();
        var cancelled = wasEditing && Gui.IsKeyPressed(ImGuiKey.Escape);
        if (cancelled || !canEdit) Reset();
        else
        {
            draft.Edited |= Gui.IsItemEdited();
            if (draft.Edited && (submitted || Gui.IsItemDeactivatedAfterEdit()))
            {
                var current = draft.IsCurrent?.Invoke() == true && draft.SourceNull == (value == null) && draft.SourceMixed == mixed
                    && draft.SourceId == reference?.Id && draft.SourceLocation == reference?.Location;
                if (current)
                {
                    try
                    {
                        // Original converter resolves the URL across all assets before the typed setter validates it.
                        var target = host.FindReferenceAsset(draft.Text);
                        var replacement = PropertyConversionFeedback.Convert<object?>(identity, () =>
                        {
                            var converted = target == null ? null : ContentAssetReference.Create(type, target);
                            ContentAssetReference.Validate(type, converted, allowsNull);
                            return converted;
                        });
                        host.AssignValue(replacement, commit);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
                    { PropertyConversionFeedback.Fail(identity, error); }
                }
                else PropertyConversionFeedback.Fail(identity);
                Reset(); draft.Original = "";
            }
        }
        if (!draft.Editing) { draft.IsCurrent = null; draft.Edited = false; }
        PropertyConversionFeedback.Draw(identity);
    }
}
