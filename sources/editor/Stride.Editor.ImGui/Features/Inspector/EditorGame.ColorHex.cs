using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<ComponentField, string> componentColorHexDrafts = [];
    private readonly Dictionary<string, string> commonColorHexDrafts = [];
    private static string ColorHex(object? value) => value switch
    {
        Color packed => ColorExtensions.RgbaToString(packed.ToRgba()),
        Color3 rgb => ColorExtensions.RgbToString(rgb.ToRgb()),
        Color4 rgba => ColorExtensions.RgbaToString(rgba.ToRgba()),
        _ => "",
    };
    // The WPF ColorConverter uses this shared parser, including its black fallback
    // for unsupported strings and its #AARRGGBB formatting for alpha colors.
    private static object ParseColorHex(string text, Type type)
    {
        var packed = ColorExtensions.StringToRgba(text);
        return type == typeof(Color) ? (object)Color.FromRgba(packed) : type == typeof(Color3) ? new Color3(packed) : new Color4(packed);
    }
    private void DrawComponentColorHex(ComponentField field, bool inherited)
    {
        var text = componentColorHexDrafts.GetValueOrDefault(field) ?? ColorHex(field.Node.Retrieve());
        Gui.SetNextItemWidth(-1);
        var submitted = Gui.InputText("##Hex", ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
        if (active) componentColorHexDrafts[field] = text; else componentColorHexDrafts.Remove(field);
        if (!submitted && !ended) return;
        Execute(() =>
        {
            var value = ParseColorHex(text, field.Type);
            if (inherited)
            {
                inheritedColorDrafts.Remove(field);
                document!.EditInheritedScalar(field, value);
            }
            else
            {
                document!.BeginComponentEdit(field);
                document.UpdateComponent(field, value);
                document.EndComponentEdit();
            }
        });
    }
    private void DrawCommonInheritedColorHex(CommonComponentProperty property, Action<Func<ComponentField, object?>>? apply = null)
        => DrawCommonColorHexValue(property.Key, property.First.Type, property.Value, property.IsMixed, value =>
        {
            if (apply != null) apply(_ => value);
            else document!.EditInheritedScalars(property.Fields, property.Fields.Select(_ => value).ToArray());
        });
    private void DrawCommonColorHexValue(string key, Type type, object? current, bool mixed, Action<object?> commit)
    {
        var text = commonColorHexDrafts.GetValueOrDefault(key) ?? (mixed ? "" : ColorHex(current));
        Gui.SetNextItemWidth(-1);
        var submitted = Gui.InputTextWithHint("##Hex", mixed ? DifferentPropertyValues : "", ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
        if (active) commonColorHexDrafts[key] = text; else commonColorHexDrafts.Remove(key);
        if (!submitted && !ended) return;
        Execute(() =>
        {
            commonInheritedColorDrafts.Remove(key);
            commit(ParseColorHex(text, type));
        });
    }
}
