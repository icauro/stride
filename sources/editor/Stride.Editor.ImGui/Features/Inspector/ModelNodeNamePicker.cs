using Hexa.NET.ImGui;
using Stride.Core.Presentation.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record ModelNodeNameChoice(string Name, int Depth);

/// <summary>Native editable/filtering node-name control with caller-owned actual model/skeleton choices.</summary>
internal sealed unsafe class ModelNodeNamePicker
{
    private sealed class ViewState
    {
        internal string Filter = "", ObservedChoices = "";
        internal int Selected;
        internal bool WasOpen, Refocus;
        internal float PageHeight;
    }
    private readonly Dictionary<string, ViewState> states = [];
    internal void Clear() => states.Clear();
    internal void Draw(string id, string? value, bool mixed, bool canEdit, Func<IReadOnlyList<ModelNodeNameChoice>> choices,
        Action<PropertyDraftInput, Func<bool>> drawText, Action<string> choose, Action<bool> finish,
        Func<string, string, bool, bool> drawChoice)
    {
        if (!states.TryGetValue(id, out var state)) states[id] = state = new();
        var activate = false; var submitted = false; var deactivated = false;
        var inputActive = false; var historyKey = ImGuiKey.None;
        var escaped = Gui.IsKeyPressed(ImGuiKey.Escape);
        // The original handler returns before its second Escape/cancel branch
        // only when there are no filtered suggestions.
        var closeOnly = escaped && state.WasOpen && !choices().Any(node => Matches(state.Filter, node.Name));
        var previousFilter = state.Filter;
        var button = Gui.GetFrameHeight();
        Gui.SetNextItemWidth(Math.Max(button, Gui.GetContentRegionAvail().X - button - Gui.GetStyle().ItemSpacing.X));
        bool Input(string control, string hint, ref string text, ImGuiInputTextFlags flags)
        {
            var refocus = state.Refocus;
            if (refocus) { Gui.SetKeyboardFocusHere(); state.Refocus = false; }
            int History(ImGuiInputTextCallbackData* data)
            {
                if (data->EventFlag == ImGuiInputTextFlags.CallbackHistory) historyKey = data->EventKey;
                return 0;
            }
            // The native empty-list Escape path leaves its text source pending.
            if (closeOnly) flags |= ImGuiInputTextFlags.ReadOnly;
            submitted = NativeStringInput.DrawWithHint(control, hint, ref text,
                flags | (canEdit && !closeOnly ? ImGuiInputTextFlags.CallbackHistory : ImGuiInputTextFlags.None),
                selectAllOnFocus: !refocus, editCallback: History);
            inputActive = Gui.IsItemActive();
            activate = canEdit && !refocus && (Gui.IsItemActivated() || Gui.IsItemEdited());
            deactivated = Gui.IsItemDeactivatedAfterEdit();
            state.Filter = text;
            // Validation below substitutes the real selected suggestion before the
            // shared controller commits, matching FilteringComboBox.Validating.
            return submitted && !state.WasOpen && !activate;
        }
        drawText(Input, () => escaped && !closeOnly);
        var minimum = Gui.GetItemRectMin();
        var maximum = Gui.GetItemRectMax();
        Gui.SameLine();
        using (ImGuiScopes.Disabled(!canEdit))
            if (Gui.ArrowButton("##Nodes", ImGuiDir.Down)) activate = true;
        maximum.X = Gui.GetItemRectMax().X;
        if (activate) Gui.OpenPopup("##NodeChoices");
        var candidates = choices().Where(node => Matches(state.Filter, node.Name)).ToArray();
        var choiceSource = string.Join("\n", candidates.Select(node => node.Depth + ":" + node.Name));
        if (state.Filter != previousFilter || state.ObservedChoices != choiceSource)
        { state.Selected = 0; state.ObservedChoices = choiceSource; }
        state.Selected = Math.Clamp(state.Selected, 0, Math.Max(0, candidates.Length - 1));
        var navigation = historyKey;
        if (canEdit && inputActive && navigation == ImGuiKey.None)
            foreach (var key in new[] { ImGuiKey.PageUp, ImGuiKey.PageDown, ImGuiKey.Home, ImGuiKey.End })
                if (Gui.IsKeyPressed(key)) { navigation = key; break; }
        if (canEdit && candidates.Length != 0 && navigation != ImGuiKey.None)
        {
            var page = Math.Max(1, (int)(state.PageHeight / Math.Max(1, Gui.GetTextLineHeightWithSpacing())));
            state.Selected = Math.Clamp(navigation switch
            {
                ImGuiKey.UpArrow => state.Selected - 1, ImGuiKey.DownArrow => state.Selected + 1,
                ImGuiKey.PageUp => state.Selected - page, ImGuiKey.PageDown => state.Selected + page,
                ImGuiKey.Home => 0, ImGuiKey.End => candidates.Length - 1, _ => state.Selected,
            }, 0, candidates.Length - 1);
        }
        Gui.SetNextWindowPos(new System.Numerics.Vector2(minimum.X, maximum.Y));
        Gui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(maximum.X - minimum.X, 0),
            new System.Numerics.Vector2(float.MaxValue, Gui.GetTextLineHeightWithSpacing() * 12));
        if (Gui.BeginPopup("##NodeChoices", ImGuiWindowFlags.NoFocusOnAppearing))
        {
            try
            {
            state.PageHeight = Math.Max(0, Gui.GetWindowHeight() - Gui.GetStyle().WindowPadding.Y * 2);
            if (canEdit && submitted && candidates.Length != 0) choose(candidates[state.Selected].Name);
            else if (canEdit && submitted) finish(false);
            if (!canEdit || submitted || escaped) Gui.CloseCurrentPopup();
            var index = 0;
            foreach (var node in candidates)
            {
                var rowIndex = index++;
                Gui.PushID(rowIndex);
                var indent = Math.Max(0, node.Depth) * 4 * Gui.GetStyle().FontScaleDpi;
                if (indent != 0) Gui.Indent(indent);
                try
                {
                    var selected = drawChoice("##Node", node.Name, rowIndex == state.Selected);
                    if (rowIndex == state.Selected && navigation != ImGuiKey.None) Gui.SetScrollHereY();
                    if (selected && canEdit) { choose(node.Name); Gui.CloseCurrentPopup(); }
                }
                finally { if (indent != 0) Gui.Unindent(indent); Gui.PopID(); }
            }
            }
            finally { Gui.EndPopup(); }
        }
        var open = Gui.IsPopupOpen("##NodeChoices");
        if (closeOnly) state.Refocus = true;
        else if (escaped && inputActive) finish(true);
        else if (!open && (deactivated || state.WasOpen)) finish(false);
        state.WasOpen = open;
    }
    private static bool Matches(string filter, string name)
        => filter.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .All(token => name.IndexOf(token, StringComparison.InvariantCultureIgnoreCase) >= 0 || token.MatchCamelCase(name));
}
