using System.Globalization;
using System.Diagnostics;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Noneditable native ComboBox closed input, using actual declaration keys.</summary>
internal static class EditorTemplateChoiceInput
{
    internal sealed class State(string[] keys)
    {
        internal readonly string[] Keys = keys;
        internal readonly NativeListTextSearch Search = new();
        internal readonly Func<int, string> Text = index => keys[index];
        internal uint Owner;
        internal int OpenedFrame = -1, HandledFrame = -1, Highlight = -1, MouseFrame = -1, SearchFocus = -1;
        internal System.Numerics.Vector2 MousePosition;
        internal uint MouseViewport;
    }
    private static void Claim(uint id)
    {
        ImGuiP.SetKeyOwner(ImGuiKey.UpArrow, id); ImGuiP.SetKeyOwner(ImGuiKey.DownArrow, id);
        ImGuiP.SetKeyOwner(ImGuiKey.LeftArrow, id); ImGuiP.SetKeyOwner(ImGuiKey.RightArrow, id);
        ImGuiP.SetKeyOwner(ImGuiKey.Home, id); ImGuiP.SetKeyOwner(ImGuiKey.End, id);
        ImGuiP.SetKeyOwner(ImGuiKey.F4, id); ImGuiP.SetKeyOwner(ImGuiKey.Space, id);
    }
    internal static unsafe ActivationScope BeforeClosed(string identity, State state, ref string value)
    {
        var context = Gui.GetCurrentContext(); var window = ImGuiP.GetCurrentWindow();
        var id = Gui.GetID(identity); var popup = ImGuiP.ImHashStr("##ComboPopup", (nuint)0, id);
        state.Owner = id;
        var closedFocus = !Gui.GetIO().AppFocusLost && context.NavId == id && context.NavWindow.Handle == window.Handle
            && !ImGuiP.IsPopupOpen(popup, ImGuiPopupFlags.None)
            && (context.ActiveId == 0 || context.ActiveId == id)
            && (context.CurrentItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) == 0;
        if (!closedFocus) return default;
        Claim(id);
        static bool Press(ImGuiKey key, uint owner) => ImGuiP.IsKeyPressed(key, ImGuiInputFlags.Repeat, owner);
        var io = Gui.GetIO(); var count = state.Keys.Length;
        var index = Array.IndexOf(state.Keys, value); var target = -1;
        var navigationHandled = false;
        var toggle = io.KeyAlt && (Press(ImGuiKey.UpArrow, id) || Press(ImGuiKey.DownArrow, id))
            || !io.KeyAlt && Press(ImGuiKey.F4, id);
        if (!toggle && !io.KeyAlt)
        {
            if (Press(ImGuiKey.UpArrow, id) || Press(ImGuiKey.LeftArrow, id)) { navigationHandled = true; if (index > 0) target = index - 1; }
            else if (Press(ImGuiKey.DownArrow, id) || Press(ImGuiKey.RightArrow, id)) { navigationHandled = true; if (index < count - 1) target = index + 1; }
            else if (Press(ImGuiKey.Home, id)) { navigationHandled = true; target = count > 0 ? 0 : -1; }
            else if (Press(ImGuiKey.End, id)) { navigationHandled = true; target = count - 1; }
        }
        if (navigationHandled || toggle) ImGuiP.NavMoveRequestCancel();
        if (target >= 0 && target < count) value = state.Keys[target];
        var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; state.Search.Expire(now);
        var hadText = false;
        if (!io.WantTextInput && !io.KeyAlt && !io.KeySuper)
            for (var character = 0; character < io.InputQueueCharacters.Size; character++)
            {
                if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
                hadText = true;
                var match = state.Search.Search(rune.ToString(), count, state.Text, now,
                    WindowsListInputTiming.SearchTimeoutSeconds, CultureInfo.GetCultureInfo("en-US"));
                if (match >= 0 && state.Search.ShouldNavigate) value = state.Keys[match];
            }
        if (hadText) io.InputQueueCharacters.Clear();
        if (toggle) { state.OpenedFrame = Gui.GetFrameCount(); state.HandledFrame = state.OpenedFrame; ImGuiP.OpenPopupEx(popup, ImGuiPopupFlags.None); }
        // Native closed Enter bubbles to the dialog default button; Space is
        // text search. Suppress the toolkit's activation for these same keys.
        return new ActivationScope(context.NavId == id && (Gui.IsKeyPressed(ImGuiKey.Enter)
            || Gui.IsKeyPressed(ImGuiKey.KeypadEnter) || Gui.IsKeyPressed(ImGuiKey.Space)));
    }
    internal static void AfterClosed(State state, ref string value)
    {
        if (Gui.IsItemFocused() && !Gui.GetIO().AppFocusLost
            && (Gui.GetCurrentContext().CurrentItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) == 0)
        {
            Claim(Gui.GetItemID());
            var io = Gui.GetIO(); var context = Gui.GetCurrentContext();
            var trail = context.InputEventsTrail;
            var position = state.MousePosition; var viewport = state.MouseViewport;
            var known = state.MouseFrame == Gui.GetFrameCount() - 1;
            // A newly focused choice can use the unchanged actual pointer only
            // when no later position transition would misattribute an event.
            if (!known)
            {
                var positionChanged = false;
                for (var n = 0; n < trail.Size; n++) positionChanged |= trail[n].Type == ImGuiInputEventType.MousePos;
                if (!positionChanged) { position = io.MousePos; viewport = io.MouseHoveredViewport; known = true; }
            }
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var window = ImGuiP.GetCurrentWindow(); var clip = window.InnerClipRect;
            var index = Array.IndexOf(state.Keys, value); var consumed = false;
            for (var n = 0; n < trail.Size; n++)
            {
                var input = trail[n];
                if (input.Type == ImGuiInputEventType.MousePos)
                { position = new(input.Union.MousePos.PosX, input.Union.MousePos.PosY); known = true; }
                else if (input.Type == ImGuiInputEventType.MouseViewport) viewport = input.Union.MouseViewport.HoveredViewportID;
                else if (input.Type == ImGuiInputEventType.MouseWheel && input.Union.MouseWheel.WheelY != 0 && known
                    && position.X >= minimum.X && position.X < maximum.X && position.Y >= minimum.Y && position.Y < maximum.Y
                    && position.X >= clip.Min.X && position.X < clip.Max.X && position.Y >= clip.Min.Y && position.Y < clip.Max.Y
                    && ((io.BackendFlags & ImGuiBackendFlags.HasMouseHoveredViewport) == 0 || viewport == Gui.GetWindowViewport().ID))
                {
                    consumed = true;
                    var next = index + (input.Union.MouseWheel.WheelY < 0 ? 1 : -1);
                    if (next >= 0 && next < state.Keys.Length) { index = next; value = state.Keys[next]; }
                }
            }
            if (consumed)
            {
                ImGuiP.SetKeyOwner(ImGuiKey.MouseWheelY, Gui.GetItemID());
                io.MouseWheel = 0;
            }
        }
        ObserveMouse(state);
    }
    internal static void ObserveMouse(State state)
    { var io = Gui.GetIO(); state.MousePosition = io.MousePos; state.MouseViewport = io.MouseHoveredViewport; state.MouseFrame = Gui.GetFrameCount(); }

    // Capture the native highlighted item separately from the committed value.
    // The popup owns dismissal even after it closes during this frame.
    internal static int BeforePopup(State state, string value)
    {
        if (Gui.IsWindowAppearing()) state.Highlight = Array.IndexOf(state.Keys, value);
        if (Gui.GetIO().AppFocusLost || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return 0;
        var owner = state.Owner; var io = Gui.GetIO();
        ImGuiP.SetKeyOwner(ImGuiKey.F4, owner); ImGuiP.SetKeyOwner(ImGuiKey.Escape, owner);
        ImGuiP.SetKeyOwner(ImGuiKey.Enter, owner); ImGuiP.SetKeyOwner(ImGuiKey.KeypadEnter, owner);
        if (io.KeyAlt) { ImGuiP.SetKeyOwner(ImGuiKey.UpArrow, owner); ImGuiP.SetKeyOwner(ImGuiKey.DownArrow, owner); }
        if (state.OpenedFrame == Gui.GetFrameCount()) return 0;
        bool Press(ImGuiKey key) => ImGuiP.IsKeyPressed(key, ImGuiInputFlags.None, owner);
        var cancel = Press(ImGuiKey.Escape);
        var commit = Press(ImGuiKey.Enter) || Press(ImGuiKey.KeypadEnter)
            || (!io.KeyAlt && Press(ImGuiKey.F4)) || (io.KeyAlt && (Press(ImGuiKey.UpArrow) || Press(ImGuiKey.DownArrow)));
        if (!cancel && !commit) return 0;
        state.HandledFrame = Gui.GetFrameCount(); ImGuiP.NavMoveRequestCancel();
        return cancel ? -1 : 1;
    }
    internal static ActivationScope BeforePopupRows(State state, int close)
    {
        state.SearchFocus = -1;
        var io = Gui.GetIO();
        if (close != 0 || io.AppFocusLost || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return default;
        ImGuiP.SetKeyOwner(ImGuiKey.Space, state.Owner);
        var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        state.Search.Expire(now);
        var hadText = false;
        if (!io.WantTextInput && !io.KeyAlt && !io.KeySuper)
            for (var character = 0; character < io.InputQueueCharacters.Size; character++)
            {
                if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
                hadText = true;
                var match = state.Search.Search(rune.ToString(), state.Keys.Length, state.Text, now,
                    WindowsListInputTiming.SearchTimeoutSeconds, CultureInfo.GetCultureInfo("en-US"));
                if (match >= 0 && state.Search.ShouldNavigate) state.SearchFocus = match;
            }
        if (hadText) { io.InputQueueCharacters.Clear(); state.HandledFrame = Gui.GetFrameCount(); }
        // A noneditable native popup treats Space as search text, not item commit.
        return new ActivationScope(!io.WantTextInput && !io.KeyAlt && !io.KeySuper
            && ImGuiP.IsKeyPressed(ImGuiKey.Space, ImGuiInputFlags.None, state.Owner));
    }
    internal static unsafe void ObserveOption(State state, int index)
    {
        var mouseFocus = Gui.IsItemHovered() && Gui.GetIO().MouseDelta != System.Numerics.Vector2.Zero;
        if (state.SearchFocus == index || mouseFocus && state.SearchFocus < 0)
        {
            var window = ImGuiP.GetCurrentWindow();
            ImGuiP.SetFocusID(Gui.GetItemID(), window); ImGuiP.FocusWindow(window);
            state.Highlight = index;
            if (state.SearchFocus == index)
            {
                // Bring only an obscured row into view, with the nearest edge.
                var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
                if (minimum.Y < window.InnerClipRect.Min.Y) Gui.SetScrollHereY(0);
                else if (maximum.Y > window.InnerClipRect.Max.Y) Gui.SetScrollHereY(1);
            }
        }
        else if (Gui.IsItemFocused()) state.Highlight = index;
    }
    internal static void AfterPopup(State state, int close, ref string value)
    {
        ObserveMouse(state);
        if (close == 0) return;
        if (close > 0 && state.Highlight >= 0 && state.Highlight < state.Keys.Length) value = state.Keys[state.Highlight];
        Gui.CloseCurrentPopup();
    }
    internal readonly struct ActivationScope : IDisposable
    {
        private readonly bool suppressed;
        private readonly uint activation, down, pressed;
        internal ActivationScope(bool suppress)
        {
            var context = Gui.GetCurrentContext(); suppressed = suppress;
            activation = context.NavActivateId; down = context.NavActivateDownId; pressed = context.NavActivatePressedId;
            if (suppressed) context.NavActivateId = context.NavActivateDownId = context.NavActivatePressedId = 0;
        }
        public void Dispose()
        {
            if (!suppressed) return;
            var context = Gui.GetCurrentContext();
            context.NavActivateId = activation; context.NavActivateDownId = down; context.NavActivatePressedId = pressed;
        }
    }
}
