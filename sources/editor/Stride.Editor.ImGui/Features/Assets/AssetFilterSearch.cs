using Hexa.NET.ImGui;
using System.Numerics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal enum AssetFilterCategory { Name, Type, Tag }
internal sealed record AssetFilterChoice(AssetFilterCategory Category, string Text, Type? Type = null)
{
    internal string Label { get; } = Category.ToString().ToLowerInvariant() + ": " + Text;
}

/// <summary>Native Name/type/Tag suggestions without changing the text input's focus.</summary>
internal sealed class AssetFilterSearch
{
    private string? pattern;
    private IReadOnlyList<Type>? catalog;
    private AssetFilterChoice[] choices = [];
    private int selected;
    private bool open;
    private IReadOnlyList<AssetFilterCategory>? allowedCategories;
    internal bool ConsumedEnter { get; private set; }
    internal bool ConsumedEscape { get; private set; }
    internal void BeginFrame() => ConsumedEnter = ConsumedEscape = false;
    internal void Reset() { pattern = null; catalog = null; allowedCategories = null; choices = []; selected = 0; open = false; ConsumedEnter = ConsumedEscape = false; }

    internal void Draw(string id, ref string text, IReadOnlyList<Type> types, Action<AssetFilterChoice, bool> commit, Action? record = null,
        IReadOnlyList<AssetFilterCategory>? categories = null, Func<string, Vector2>? measureText = null,
        Action<string, Vector2, Vector2, Vector2, uint>? paintText = null)
    {
        BeginFrame();
        var enter = Gui.InputTextWithHint(id, EditorLocalization.Text("Add a filter..."), ref text, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var active = Gui.IsItemActive();
        var origin = Gui.GetItemRectMin(); var edge = Gui.GetItemRectMax();
        record?.Invoke();
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Filter assets by name, type or tag"));
        if ((active || Gui.IsItemFocused()) && Gui.IsKeyPressed(ImGuiKey.Escape)) { text = ""; open = false; ConsumedEscape = true; return; }
        if (pattern != text || !ReferenceEquals(catalog, types) || !ReferenceEquals(allowedCategories, categories))
        {
            pattern = text; catalog = types; allowedCategories = categories; selected = 0;
            var query = text;
            bool Allows(AssetFilterCategory category) => categories == null || categories.Contains(category);
            choices = text.Length == 0 ? [] : (Allows(AssetFilterCategory.Name) ? new[] { new AssetFilterChoice(AssetFilterCategory.Name, text) } : [])
                .Concat(types.Where(type => Allows(AssetFilterCategory.Type) && (Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(type => new AssetFilterChoice(AssetFilterCategory.Type, Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name, type)))
                .Concat(Allows(AssetFilterCategory.Tag) ? [new AssetFilterChoice(AssetFilterCategory.Tag, text)] : Array.Empty<AssetFilterChoice>()).ToArray();
        }
        if (active && choices.Length > 0) open = true;
        if (active)
        {
            if (Gui.IsKeyPressed(ImGuiKey.UpArrow)) selected = Math.Max(0, selected - 1);
            if (Gui.IsKeyPressed(ImGuiKey.DownArrow)) selected = Math.Min(choices.Length - 1, selected + 1);
            if (Gui.IsKeyPressed(ImGuiKey.PageUp)) selected = Math.Max(0, selected - 8);
            if (Gui.IsKeyPressed(ImGuiKey.PageDown)) selected = Math.Min(choices.Length - 1, selected + 8);
            if (Gui.IsKeyPressed(ImGuiKey.Home)) selected = 0;
            if (Gui.IsKeyPressed(ImGuiKey.End)) selected = choices.Length - 1;
        }
        if (enter && choices.Length > 0)
        {
            ConsumedEnter = true;
            commit(choices[Math.Clamp(selected, 0, choices.Length - 1)], Gui.GetIO().KeyShift);
            text = ""; open = false; return;
        }
        if (!open || choices.Length == 0) return;
        Gui.SetNextWindowPos(new Vector2(origin.X, edge.Y));
        Vector2 Measure(string value) => measureText?.Invoke(value) ?? Gui.CalcTextSize(value);
        var width = Math.Max(edge.X - origin.X, choices.Max(choice => Measure(choice.Label).X) + Gui.GetStyle().WindowPadding.X * 2 + Gui.GetStyle().ScrollbarSize);
        var rowHeight = Math.Max(Gui.GetFrameHeight(), choices.Max(choice => Measure(choice.Label).Y) + Gui.GetStyle().FramePadding.Y * 2);
        Gui.SetNextWindowSize(new Vector2(width, Math.Min(8, choices.Length) * (rowHeight + Gui.GetStyle().ItemSpacing.Y) + Gui.GetStyle().WindowPadding.Y * 2));
        if (Gui.Begin("###AssetFilterSuggestions" + id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoDocking))
        {
            if (!active && !Gui.IsWindowHovered()) open = false;
            for (var index = 0; index < choices.Length; index++)
            {
                Gui.PushID(index);
                var labelOrigin = Gui.GetCursorScreenPos();
                var clicked = Gui.Selectable("##Choice", index == selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight));
                var labelSize = Measure(choices[index].Label); labelOrigin.Y += Math.Max(0, (rowHeight - labelSize.Y) * .5f);
                if (paintText != null) paintText(choices[index].Label, labelOrigin, Gui.GetItemRectMin(), Gui.GetItemRectMax(), Gui.GetColorU32(ImGuiCol.Text));
                else Gui.GetWindowDrawList().AddText(labelOrigin, Gui.GetColorU32(ImGuiCol.Text), choices[index].Label);
                if (clicked)
                {
                    commit(choices[index], Gui.GetIO().KeyShift); text = ""; open = false;
                }
                if (active && index == selected && (Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.DownArrow)
                    || Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown)
                    || Gui.IsKeyPressed(ImGuiKey.Home) || Gui.IsKeyPressed(ImGuiKey.End))) Gui.SetScrollHereY();
                Gui.PopID();
            }
        }
        ImGuiScopes.EndWindow();
    }
}
