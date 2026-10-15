using System.Globalization;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawRangedPropertyText(IReadOnlyList<ComponentField> fields, int axis, object? value, bool mixed, ref string text)
    {
        if (axis >= 0 || fields.Any(field => !NativeRangedNumericControl.Matches(field.DeclaredType, field.EffectiveNumericRange))) return false;
        var commit = NativeRangedNumericControl.Draw(fields[0].Node, fields[0].Type, value, mixed,
            fields.Select(field => field.EffectiveNumericRange).ToArray(), UiScale(1), out var display, out _);
        if (display != null && (mixed || !Equals(display, value))) text = Convert.ToString(display, CultureInfo.InvariantCulture) ?? "";
        return commit;
    }
}
