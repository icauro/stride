using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Stack-only native UI scopes; dispose on the same UI frame and thread.</summary>
internal static partial class ImGuiScopes
{
    internal static WindowScope Window(string title, ref bool visible) => new(title, ref visible);
    internal static WindowScope Window(string title, ImGuiWindowFlags flags) => new(title, flags);
    internal static DisabledScope Disabled(bool disabled) => new(disabled);
    internal static FontScope Font(float size) => new(size);
    internal static StyleScope Style(ImGuiStyleVar variable, System.Numerics.Vector2 value) => new(variable, value);
    internal static StyleScope Style(ImGuiStyleVar variable, float value) => new(variable, value);
    internal static ColorScope Color(ImGuiCol color, System.Numerics.Vector4 value) => new(color, value);
    internal static ModalScope Modal(string title, ImGuiWindowFlags flags) => new(title, flags);
    internal static TabBarScope TabBar(string id, ImGuiTabBarFlags flags = ImGuiTabBarFlags.None) => new(id, flags);
    internal static TabItemScope TabItem(string title, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None) => new(title, flags);
    internal static ChildScope Child(string id, System.Numerics.Vector2 size, ImGuiChildFlags flags, ImGuiWindowFlags windowFlags) => new(id, size, flags, windowFlags);
    internal static ComboScope Combo(string label, string preview) => new(label, preview);
    internal static GroupScope Group() => new();
    internal static TextWrapScope TextWrap(float position) => new(position);

    internal readonly ref struct GroupScope
    {
        public GroupScope() => Gui.BeginGroup();
        public void Dispose() { TraceLayoutBoundary("EndGroup"); Gui.EndGroup(); }
    }

    internal readonly ref struct TextWrapScope
    {
        internal TextWrapScope(float position) => Gui.PushTextWrapPos(position);
        public void Dispose() => Gui.PopTextWrapPos();
    }

    internal readonly ref struct WindowScope
    {
        internal bool DrawContents { get; }
        internal WindowScope(string title, ref bool visible) => DrawContents = Gui.Begin(title, ref visible);
        internal WindowScope(string title, ImGuiWindowFlags flags) => DrawContents = Gui.Begin(title, flags);
        public void Dispose() => EndWindow();
    }

    internal readonly ref struct DisabledScope
    {
        internal DisabledScope(bool disabled) => Gui.BeginDisabled(disabled);
        public void Dispose() => Gui.EndDisabled();
    }

    internal readonly ref struct FontScope
    {
        internal FontScope(float size) => Gui.PushFont(Gui.GetFont(), size);
        public void Dispose() => Gui.PopFont();
    }

    internal readonly ref struct StyleScope
    {
        internal StyleScope(ImGuiStyleVar variable, float value) => Gui.PushStyleVar(variable, value);
        internal StyleScope(ImGuiStyleVar variable, System.Numerics.Vector2 value) => Gui.PushStyleVar(variable, value);
        public void Dispose() => Gui.PopStyleVar();
    }

    internal readonly ref struct ColorScope
    {
        internal ColorScope(ImGuiCol color, System.Numerics.Vector4 value) => Gui.PushStyleColor(color, value);
        public void Dispose() => Gui.PopStyleColor();
    }

    internal readonly ref struct ModalScope
    {
        internal bool DrawContents { get; }
        internal ModalScope(string title, ImGuiWindowFlags flags) => DrawContents = Gui.BeginPopupModal(title, flags);
        public void Dispose() { if (DrawContents) { TraceLayoutBoundary("EndPopup"); Gui.EndPopup(); } }
    }

    internal readonly ref struct TabBarScope
    {
        internal bool DrawContents { get; }
        internal TabBarScope(string id, ImGuiTabBarFlags flags) => DrawContents = Gui.BeginTabBar(id, flags);
        public void Dispose()
        {
            if (!DrawContents) return;
            TraceLayoutPhase("before-EndTabBar");
            Gui.EndTabBar();
            TraceLayoutPhase("after-EndTabBar");
        }
    }

    internal readonly ref struct TabItemScope
    {
        internal bool DrawContents { get; }
        internal TabItemScope(string title, ImGuiTabItemFlags flags) => DrawContents = Gui.BeginTabItem(title, flags);
        public void Dispose() { if (DrawContents) Gui.EndTabItem(); }
    }

    internal readonly ref struct ChildScope
    {
        internal bool DrawContents { get; }
        internal ChildScope(string id, System.Numerics.Vector2 size, ImGuiChildFlags flags, ImGuiWindowFlags windowFlags)
            => DrawContents = Gui.BeginChild(id, size, flags, windowFlags);
        public void Dispose() => EndChild();
    }

    internal readonly ref struct ComboScope
    {
        internal bool DrawContents { get; }
        internal ComboScope(string label, string preview) => DrawContents = Gui.BeginCombo(label, preview);
        public void Dispose() { if (DrawContents) { TraceLayoutBoundary("EndCombo"); Gui.EndCombo(); } }
    }
}
