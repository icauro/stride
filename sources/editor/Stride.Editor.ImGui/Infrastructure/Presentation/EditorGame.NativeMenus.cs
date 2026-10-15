using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool BeginNativeMainMenuBar()
    {
        var padding = Gui.GetStyle().FramePadding;
        var extra = Math.Max(0, MeasureNativeLabel("Mg").Y - Gui.GetFontSize()) * .5f;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding.X, padding.Y + extra));
        if (Gui.BeginMainMenuBar()) return true;
        Gui.PopStyleVar(); return false;
    }
    private static void EndNativeMainMenuBar()
    {
        Gui.EndMainMenuBar(); Gui.PopStyleVar();
    }
    // Menu interaction follows Dear ImGui 1.92.2; native text supplies the same layout geometry.
    private static unsafe bool IsNativeRootMenuSetOpen(ImGuiWindowPtr window)
    {
        var context = Gui.GetCurrentContext();
        if (context.OpenPopupStack.Size <= context.BeginPopupStack.Size || (window.Flags & ImGuiWindowFlags.ChildMenu) != 0) return false;
        var popup = context.OpenPopupStack[context.BeginPopupStack.Size];
        return window.DC.NavLayerCurrent == (ImGuiNavLayer)popup.ParentNavLayer && popup.Window != null
            && (popup.Window->Flags & ImGuiWindowFlags.ChildMenu) != 0
            && ImGuiP.IsWindowChildOf(new ImGuiWindowPtr(popup.Window), window, true, false);
    }

    private bool DrawNativeMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
    {
        if (!CanDrawNativeWidgetText) return Gui.MenuItem(label, shortcut, selected, enabled);
        return DrawNativeMenuLeaf(label, shortcut, selected, enabled);
    }
    private bool DrawNativeMenuItem(string label, string? shortcut, ref bool selected, bool enabled = true)
    {
        if (!DrawNativeMenuItem(label, shortcut, selected, enabled)) return false;
        selected = !selected; return true;
    }
    private bool DrawNativeLiteralMenuItem(string identity, string text, bool enabled = true)
    {
        if (CanDrawNativeWidgetText) return DrawNativeMenuLeaf(identity, null, false, enabled, text);
        Gui.PushID(identity);
        try
        {
            using var disabled = ImGuiScopes.Disabled(!enabled);
            var size = Gui.CalcTextSize(text);
            var clicked = Gui.Selectable("##literal-menu", false,
                (ImGuiSelectableFlags)ImGuiSelectableFlagsPrivate.SpanAvailWidth, size);
            if (Gui.IsItemVisible()) Gui.GetWindowDrawList().AddText(Gui.GetItemRectMin(), Gui.GetColorU32(ImGuiCol.Text), text);
            return clicked;
        }
        finally { Gui.PopID(); }
    }
    private unsafe bool DrawNativeMenuLeaf(string label, string? shortcut, bool selected, bool enabled, string? literalText = null)
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var style = Gui.GetStyle(); var context = Gui.GetCurrentContext();
        var rootOpen = IsNativeRootMenuSetOpen(window);
        if (rootOpen) Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.NoWindowHoverableCheck, true);
        Gui.PushID(label);
        if (!enabled) Gui.BeginDisabled();
        try
        {
            var visible = literalText ?? NativeVisibleWidgetLabel(label);
            var textSize = MeasureNativeLabel(visible);
            var hasShortcut = !string.IsNullOrEmpty(shortcut);
            var shortcutSize = hasShortcut ? MeasureNativeLabel(shortcut!) : Vector2.Zero;
            var flags = (ImGuiSelectableFlags)(ImGuiSelectableFlagsPrivate.SelectOnRelease | ImGuiSelectableFlagsPrivate.NoSetKeyOwner | ImGuiSelectableFlagsPrivate.SetNavIdOnHover);
            var position = window.DC.CursorPos;
            bool pressed;
            if (window.DC.LayoutType == ImGuiLayoutType.Horizontal)
            {
                var spacing = style.ItemSpacing;
                window.Handle->DC.CursorPos.X += MathF.Truncate(spacing.X * .5f);
                var origin = window.DC.CursorPos + new Vector2(window.DC.MenuColumns.OffsetLabel, window.DC.CurrLineTextBaseOffset);
                Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X * 2, spacing.Y));
                try { pressed = Gui.Selectable("", selected, flags, textSize); }
                finally { Gui.PopStyleVar(); }
                if (Gui.IsItemVisible()) PaintNativeWidget(NativeWidgetLayout(visible), origin, Gui.GetItemRectMin(), Gui.GetItemRectMax());
                LogNativeWidgetText(visible, origin);
                window.Handle->DC.CursorPos.X += MathF.Truncate(-spacing.X * .5f);
            }
            else
            {
                var width = ImGuiP.DeclColumns(ref window.Handle->DC.MenuColumns, 0, textSize.X, shortcutSize.X, MathF.Truncate(Gui.GetFontSize() * 1.2f));
                var stretch = Math.Max(0, Gui.GetContentRegionAvail().X - width);
                var height = Math.Max(textSize.Y, shortcutSize.Y);
                pressed = Gui.Selectable("", false, flags | (ImGuiSelectableFlags)ImGuiSelectableFlagsPrivate.SpanAvailWidth, new Vector2(width, height));
                var origin = position + new Vector2(window.DC.MenuColumns.OffsetLabel, 0);
                if (Gui.IsItemVisible())
                {
                    PaintNativeWidget(NativeWidgetLayout(visible), origin, Gui.GetItemRectMin(), Gui.GetItemRectMax());
                    if (hasShortcut)
                        PaintNativeWidget(NativeWidgetLayout(shortcut!, color: Gui.GetColorU32(ImGuiCol.TextDisabled)), position + new Vector2(window.DC.MenuColumns.OffsetShortcut + stretch, 0), Gui.GetItemRectMin(), Gui.GetItemRectMax());
                    if (selected) ImGuiP.RenderCheckMark(window.DrawList, position + new Vector2(window.DC.MenuColumns.OffsetMark + stretch + Gui.GetFontSize() * .4f, Gui.GetFontSize() * .067f), Gui.GetColorU32(ImGuiCol.Text), Gui.GetFontSize() * .866f);
                }
                LogNativeWidgetText(visible, origin);
                if (hasShortcut) LogNativeWidgetText(shortcut!, position + new Vector2(window.DC.MenuColumns.OffsetShortcut + stretch, 0));
            }
            return pressed;
        }
        finally
        {
            if (!enabled) Gui.EndDisabled();
            Gui.PopID();
            if (rootOpen) Gui.PopItemFlag();
        }
    }

    private unsafe bool BeginNativeMenu(string label, bool enabled = true)
    {
        if (!CanDrawNativeWidgetText) return Gui.BeginMenu(label, enabled);
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var context = Gui.GetCurrentContext(); var style = Gui.GetStyle(); var identity = Gui.GetID(label);
        var open = ImGuiP.IsPopupOpen(identity, ImGuiPopupFlags.None);
        var windowFlags = ImGuiWindowFlags.ChildMenu | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNavFocus;
        if ((window.Flags & ImGuiWindowFlags.ChildMenu) != 0) windowFlags |= ImGuiWindowFlags.ChildWindow;
        ref var submitted = ref context.Handle->MenusIdSubmittedThisFrame;
        for (var index = 0; index < submitted.Size; index++)
            if (submitted[index] == identity)
            {
                if (open) return ImGuiP.BeginPopupMenuEx(identity, label, windowFlags);
                context.NextWindowData.HasFlags = ImGuiNextWindowDataFlags.None; return false;
            }
        submitted.PushBack(identity);
        var rootOpen = IsNativeRootMenuSetOpen(window);
        if (rootOpen) Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.NoWindowHoverableCheck, true);
        var position = window.DC.CursorPos; Vector2 popupPosition;
        Gui.PushID(label);
        var idPushed = true; var hoverFlagPushed = rootOpen;
        try
        {
            if (!enabled) Gui.BeginDisabled();
            bool pressed;
            try
            {
                var visible = NativeVisibleWidgetLabel(label); var textSize = MeasureNativeLabel(visible);
                var flags = (ImGuiSelectableFlags)(ImGuiSelectableFlagsPrivate.NoHoldingActiveId | ImGuiSelectableFlagsPrivate.NoSetKeyOwner | ImGuiSelectableFlagsPrivate.SelectOnClick) | ImGuiSelectableFlags.NoAutoClosePopups;
                if (window.DC.LayoutType == ImGuiLayoutType.Horizontal)
                {
                    var spacing = style.ItemSpacing;
                    popupPosition = new(position.X - 1 - MathF.Truncate(spacing.X * .5f), position.Y - style.FramePadding.Y + window.MenuBarHeight);
                    window.Handle->DC.CursorPos.X += MathF.Truncate(spacing.X * .5f);
                    var origin = window.DC.CursorPos + new Vector2(window.DC.MenuColumns.OffsetLabel, window.DC.CurrLineTextBaseOffset);
                    Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X * 2, spacing.Y));
                    try { pressed = Gui.Selectable("", open, flags, textSize); }
                    finally { Gui.PopStyleVar(); }
                    if (Gui.IsItemVisible()) PaintNativeWidget(NativeWidgetLayout(visible), origin, Gui.GetItemRectMin(), Gui.GetItemRectMax());
                    LogNativeWidgetText(NativeVisibleWidgetLabel(label), origin);
                    window.Handle->DC.CursorPos.X += MathF.Truncate(-spacing.X * .5f);
                }
                else
                {
                    popupPosition = new(position.X, position.Y - style.WindowPadding.Y);
                    var width = ImGuiP.DeclColumns(ref window.Handle->DC.MenuColumns, 0, textSize.X, 0, MathF.Truncate(Gui.GetFontSize() * 1.2f));
                    var extra = Math.Max(0, Gui.GetContentRegionAvail().X - width);
                    var origin = position + new Vector2(window.DC.MenuColumns.OffsetLabel, window.DC.CurrLineTextBaseOffset);
                    pressed = Gui.Selectable("", open, flags | (ImGuiSelectableFlags)ImGuiSelectableFlagsPrivate.SpanAvailWidth, new Vector2(width, textSize.Y));
                    if (Gui.IsItemVisible())
                    {
                        PaintNativeWidget(NativeWidgetLayout(visible), origin, Gui.GetItemRectMin(), Gui.GetItemRectMax());
                        ImGuiP.RenderArrow(window.DrawList, position + new Vector2(window.DC.MenuColumns.OffsetMark + extra + Gui.GetFontSize() * .3f, 0), Gui.GetColorU32(ImGuiCol.Text), ImGuiDir.Right);
                    }
                    LogNativeWidgetText(NativeVisibleWidgetLabel(label), origin);
                }
            }
            finally { if (!enabled) Gui.EndDisabled(); }
            var hovered = context.HoveredId == identity && enabled && !context.NavHighlightItemUnderNav;
            if (rootOpen) { Gui.PopItemFlag(); hoverFlagPushed = false; }
            var wantOpen = false; var initializeNavigation = false; var wantClose = false;
            if (window.DC.LayoutType == ImGuiLayoutType.Vertical)
            {
                var movingToChild = IsNativePointerMovingToSubmenu(window);
                if (open && !hovered && context.HoveredWindow == window && !movingToChild && !context.NavHighlightItemUnderNav && context.ActiveId == 0) wantClose = true;
                if (!open && pressed || !open && hovered && !movingToChild
                    || !open && hovered && context.HoveredIdTimer >= .3f && context.MouseStationaryTimer >= .3f) wantOpen = true;
                if (context.NavId == identity && context.NavMoveDir == ImGuiDir.Right)
                { wantOpen = initializeNavigation = true; ImGuiP.NavMoveRequestCancel(); ImGuiP.SetNavCursorVisibleAfterMove(); }
            }
            else
            {
                if (open && pressed && rootOpen) { wantClose = true; wantOpen = open = false; }
                else if (pressed || hovered && rootOpen && !open) wantOpen = true;
                else if (context.NavId == identity && context.NavMoveDir == ImGuiDir.Down) { wantOpen = true; ImGuiP.NavMoveRequestCancel(); }
            }
            if (!enabled) wantClose = true;
            if (wantClose && ImGuiP.IsPopupOpen(identity, ImGuiPopupFlags.None)) ImGuiP.ClosePopupToLevel(context.BeginPopupStack.Size, true);
            Gui.PopID(); idPushed = false;
            if (wantOpen && !open && context.OpenPopupStack.Size > context.BeginPopupStack.Size) Gui.OpenPopup(label);
            else if (wantOpen) { open = true; Gui.OpenPopup(label, ImGuiPopupFlags.NoReopen); }
            if (!open) { context.NextWindowData.HasFlags = ImGuiNextWindowDataFlags.None; return false; }
            var lastItem = context.LastItemData;
            Gui.SetNextWindowPos(popupPosition, ImGuiCond.Always);
            Gui.PushStyleVar(ImGuiStyleVar.ChildRounding, style.PopupRounding);
            try { open = ImGuiP.BeginPopupMenuEx(identity, label, windowFlags); }
            finally { Gui.PopStyleVar(); }
            if (open)
            {
                if (wantOpen && initializeNavigation && !context.NavInitRequest)
                { ImGuiP.FocusWindow(context.CurrentWindow, ImGuiFocusRequestFlags.UnlessBelowModal); ImGuiP.NavInitWindow(context.CurrentWindow, false); }
                context.LastItemData = lastItem;
                if (context.HoveredWindow == window) context.LastItemData.StatusFlags |= ImGuiItemStatusFlags.HoveredWindow;
            }
            return open;
        }
        finally
        {
            if (idPushed) Gui.PopID();
            if (hoverFlagPushed) Gui.PopItemFlag();
        }
    }

    private static unsafe bool IsNativePointerMovingToSubmenu(ImGuiWindowPtr window)
    {
        var context = Gui.GetCurrentContext();
        if (context.HoveredWindow != window || context.BeginPopupStack.Size >= context.OpenPopupStack.Size) return false;
        var child = context.OpenPopupStack[context.BeginPopupStack.Size].Window;
        if (child == null || child->ParentWindow != window.Handle) return false;
        var unit = Gui.GetFontSize(); var direction = window.Pos.X < child->Pos.X ? 1f : -1f;
        var a = Gui.GetIO().MousePos - Gui.GetIO().MouseDelta;
        var b = child->Pos + new Vector2(direction > 0 ? 0 : child->Size.X, 0);
        var c = b + new Vector2(0, child->Size.Y);
        var slack = Math.Clamp(Math.Abs(a.X - b.X) * .3f, unit * .5f, unit * 2.5f);
        a.X -= direction * .5f; b.X += direction * unit; c.X += direction * unit;
        b.Y = a.Y + Math.Max(b.Y - slack - a.Y, -unit * 8);
        c.Y = a.Y + Math.Min(c.Y + slack - a.Y, unit * 8);
        return ImGuiP.ImTriangleContainsPoint(a, b, c, Gui.GetIO().MousePos);
    }
}
