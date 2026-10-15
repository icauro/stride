using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void HandleSpriteFrameListKeyboard(AssetMetadataDocument document)
    {
        if (!Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || Gui.GetIO().WantTextInput || Gui.IsAnyItemActive()) return;
        var rows = document.SpriteSettingsFrames; if (rows.Count == 0) return;
        var state = spriteFrameLists.GetOrCreateValue(document); var io = Gui.GetIO();
        PumpSpriteListTextSearch(document, state);
        if (io.KeyAlt || io.KeySuper) return;
        EditorListKeyboardInput.Claim("##SpriteListKeys");
        if (Gui.IsKeyPressed(ImGuiKey.LeftArrow) && Gui.GetScrollX() > 0)
        { Gui.SetScrollX(Math.Max(0, Gui.GetScrollX() - 16 * Gui.GetStyle().FontScaleDpi)); return; }
        if (Gui.IsKeyPressed(ImGuiKey.RightArrow) && Gui.GetScrollX() < Gui.GetScrollMaxX())
        { Gui.SetScrollX(Math.Min(Gui.GetScrollMaxX(), Gui.GetScrollX() + 16 * Gui.GetStyle().FontScaleDpi)); return; }
        var current = -1;
        for (var index = 0; index < rows.Count; index++) if (rows[index].Id == state.KeyboardFocus) { current = index; break; }
        if (current < 0)
        {
            var selected = document.LastSelectedSpriteFrame;
            for (var index = 0; index < rows.Count; index++) if (rows[index].Id == selected?.Id) { current = index; break; }
            if (current < 0) current = 0;
        }
        if (!io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.F2, false) && document.LastSelectedSpriteFrame is { } rename)
        {
            state.KeyboardFocus = rename.Id; state.ScrollFocus = true;
            BeginSpriteRowRename(document, rename);
            state.RenameScrollFrame = Gui.GetFrameCount() + 1; return;
        }
        var target = current; var navigate = false;
        var page = Math.Max(1, (int)(Gui.GetContentRegionAvail().Y / Math.Max(1, Gui.GetFrameHeightWithSpacing())));
        if (Gui.IsKeyPressed(ImGuiKey.UpArrow)) { target--; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.DownArrow)) { target++; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.Home)) { target = 0; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.End)) { target = rows.Count - 1; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.PageUp)) { target -= page; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.PageDown)) { target += page; navigate = true; }
        if (navigate)
        {
            target = Math.Clamp(target, 0, rows.Count - 1); var id = rows[target].Id;
            state.KeyboardFocus = id; state.ApplyFocus = state.ScrollFocus = true;
            // Native MakeKeyboardSelection does not change the anchor or
            // selection for Control-only focus navigation.
            if (io.KeyShift || !io.KeyCtrl) Execute(() => SelectSpriteFrameAfterPendingEdits(document, id, io.KeyCtrl && io.KeyShift, io.KeyShift));
        }
        else if (Gui.IsKeyPressed(ImGuiKey.Space, false) && !(io.KeyCtrl && io.KeyShift) && state.Search.Prefix.Length == 0)
        {
            var id = rows[current].Id; state.KeyboardFocus = id;
            Execute(() => SelectSpriteFrameAfterPendingEdits(document, id, io.KeyCtrl, io.KeyShift));
        }
        else if (io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.Slash, false)) Execute(() => { ValidatePendingPropertyStrings(); document.SelectAllSpriteFrames(); });
        else if (io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.Backslash, false))
        { var id = rows[current].Id; Execute(() => SelectSpriteFrameAfterPendingEdits(document, id)); }
    }
    private void PrepareSpriteFrameKeyboardRow(AssetMetadataDocument document, SpriteSettingFrame frame)
    {
        if (spriteFrameLists.TryGetValue(document, out var state) && state.ApplyFocus && state.KeyboardFocus == frame.Id)
            Gui.SetKeyboardFocusHere();
    }
    private void ObserveSpriteFrameKeyboardRow(AssetMetadataDocument document, SpriteSettingFrame frame)
    {
        var state = spriteFrameLists.GetOrCreateValue(document);
        if (Gui.IsItemFocused())
        {
            if (!state.ApplyFocus && state.KeyboardFocus != frame.Id && !Gui.IsMouseDown(ImGuiMouseButton.Left) && !Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                var io = Gui.GetIO();
                if (!io.KeyCtrl || io.KeyShift) Execute(() => SelectSpriteFrameAfterPendingEdits(document, frame.Id, io.KeyCtrl && io.KeyShift, io.KeyShift));
            }
            state.KeyboardFocus = frame.Id;
        }
        if (state.KeyboardFocus != frame.Id) return;
        state.ApplyFocus = false;
        if (!state.ScrollFocus) return;
        var top = Gui.GetWindowPos().Y + Gui.GetStyle().WindowPadding.Y;
        var bottom = Gui.GetWindowPos().Y + Gui.GetWindowSize().Y - Gui.GetStyle().WindowPadding.Y;
        if (Gui.GetItemRectMin().Y < top) Gui.SetScrollHereY(0);
        else if (Gui.GetItemRectMax().Y > bottom) Gui.SetScrollHereY(1);
        state.ScrollFocus = false;
    }
}
