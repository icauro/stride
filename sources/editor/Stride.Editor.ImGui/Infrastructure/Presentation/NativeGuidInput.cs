using System.Runtime.CompilerServices;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static unsafe class NativeGuidInput
{
    private const string Template = "00000000-0000-0000-0000-000000000000";
    private static readonly int[] GroupSizes = [8, 4, 4, 4, 12];
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private sealed class Draft
    {
        internal Guid Observed;
        internal bool Initialized, Mixed, Invalid;
        internal int PreviousLength;
        internal readonly ImGuiInputTextCallback Callback;
        internal Draft() => Callback = Mask;
        internal string Text = "";
        internal void Set(string text)
        {
            Text = text; PreviousLength = text.Length; Invalid = false;
        }
        private int Mask(ImGuiInputTextCallbackData* data)
        {
            if (data->EventFlag != ImGuiInputTextFlags.CallbackEdit) return 0;
            var text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data->Buf, data->BufTextLen));
            var grew = text.Length > PreviousLength;
            var raw = text.Replace("-", "").Trim('{', '}', '(', ')', ' ');
            Invalid = raw.Length > 32 || raw.Any(character => !char.IsAsciiHexDigit(character));
            var formatted = Invalid ? text : Format(raw, grew);
            var cursor = Math.Clamp(data->CursorPos, 0, data->BufTextLen);
            var prefix = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data->Buf, cursor));
            if (formatted != text)
            {
                var rawBefore = prefix.Count(char.IsAsciiHexDigit);
                var position = Invalid ? Math.Min(prefix.Length, formatted.Length) : CaretAfter(formatted, rawBefore, grew);
                Gui.DeleteChars(ref *data, 0, data->BufTextLen);
                Gui.InsertChars(ref *data, 0, formatted);
                data->CursorPos = data->SelectionStart = data->SelectionEnd = Encoding.UTF8.GetByteCount(formatted.AsSpan(0, position));
            }
            PreviousLength = formatted.Length; return 0;
        }
    }
    private static string Format(string raw, bool appendTrailingDash)
    {
        var result = new StringBuilder(raw.Length + 4); var taken = 0;
        foreach (var size in GroupSizes)
        {
            if (taken >= raw.Length) break;
            var count = Math.Min(size, raw.Length - taken); result.Append(raw, taken, count); taken += count;
            if (count == size && taken < 32 && (taken < raw.Length || appendTrailingDash)) result.Append('-');
        }
        return result.ToString();
    }
    private static int CaretAfter(string formatted, int rawBefore, bool grew)
    {
        var position = 0; var seen = 0;
        while (position < formatted.Length && seen < rawBefore) { if (formatted[position] != '-') seen++; position++; }
        if (grew) while (position < formatted.Length && formatted[position] == '-') position++;
        return position;
    }
    private static bool Render(Draft draft, string control, string hint, ImGuiInputTextFlags flags)
    {
        var id = Gui.GetID(control);
        var invalidTint = draft.Invalid;
        if (invalidTint) Gui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(.804f, .361f, .361f, 1));
        var text = draft.Text;
        var submitted = NativeStringInput.DrawWithHint(control, hint, ref text,
            flags | ((flags & ImGuiInputTextFlags.ReadOnly) == 0 ? ImGuiInputTextFlags.CallbackEdit : ImGuiInputTextFlags.None), editCallback: draft.Callback);
        draft.Text = text;
        if (invalidTint) Gui.PopStyleColor();
        if (Gui.IsItemActive() && text.Length < Template.Length)
        {
            var input = ImGuiP.GetInputTextState(id); var scroll = input.IsNull ? 0 : input.Scroll.X;
            var minimum = Gui.GetItemRectMin() + Gui.GetStyle().FramePadding;
            var maximum = Gui.GetItemRectMax() - Gui.GetStyle().FramePadding;
            var position = minimum + new System.Numerics.Vector2(Gui.CalcTextSize(text).X - scroll, 0);
            var draw = Gui.GetWindowDrawList(); draw.PushClipRect(minimum, maximum, true);
            draw.AddText(position, Gui.GetColorU32(new System.Numerics.Vector4(127f / 255, 127f / 255, 127f / 255, 128f / 255)), Template[text.Length..]); draw.PopClipRect();
        }
        GC.KeepAlive(draft.Callback); return submitted;
    }
    internal static bool DrawDraft(object owner, string control, string hint, ref string text, ImGuiInputTextFlags flags)
    {
        var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID(control);
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        if (draft.Text != text)
        {
            draft.Set(text);
            var input = ImGuiP.GetInputTextState(id);
            if (input.Handle != null) ImGuiP.ReloadUserBufAndKeepSelection(input);
        }
        var submitted = Render(draft, control, hint, flags); text = draft.Text; return submitted;
    }
    internal static bool Draw(object owner, Guid value, bool mixed, bool canEdit, out Guid updated)
    {
        updated = value; var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID("##Guid");
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        if (!draft.Initialized || draft.Observed != value || draft.Mixed != mixed)
        { draft.Initialized = true; draft.Observed = value; draft.Mixed = mixed; draft.Set(mixed ? "" : value.ToString()); }
        var submitted = Render(draft, "##Guid", mixed ? "(Different values)" : "", ImGuiInputTextFlags.EnterReturnsTrue | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
        var finished = Gui.IsItemDeactivatedAfterEdit(); var text = draft.Text;
        var changed = false;
        if (canEdit && (submitted || finished))
        {
            try
            {
                updated = PropertyConversionFeedback.Convert(id, () => Guid.Parse(text));
                draft.Invalid = false; PropertyConversionFeedback.Validate(id); changed = true;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                PropertyConversionFeedback.Fail(id, error); draft.Set(mixed ? "" : value.ToString());
            }
        }
        PropertyConversionFeedback.Draw(id); GC.KeepAlive(draft.Callback); return changed;
    }
}
