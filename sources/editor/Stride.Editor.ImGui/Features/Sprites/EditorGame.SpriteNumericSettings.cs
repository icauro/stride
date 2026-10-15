using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static Type SpriteCoordinateType(Type type) => type == typeof(Rectangle) ? typeof(int)
        : type == typeof(Vector2) || type == typeof(Vector4) ? typeof(float) : type;
    private void DrawSpriteNumericSetting(AssetMetadataDocument document, SpriteSettingField field)
    {
        DrawSpritePropertyLabel(document, field);
        var dimensions = field.Type == typeof(Vector2) ? 2 : field.Type == typeof(Vector4) || field.Type == typeof(Rectangle) ? 4 : 1;
        var type = SpriteCoordinateType(field.Type);
        var ranges = dimensions == 1 ? new[] { document.SpriteNumericRange(field) } : document.SpriteAxisNumericRanges(field);
        for (var axis = 0; axis < dimensions; axis++)
        {
            Gui.PushID(axis);
            try
            {
                var coordinate = axis;
                bool Editable() => CanEditSpriteSettingDraft(document, field)
                    && (dimensions == 1 || !document.ExtensionNumericAxisReadOnly(field.Node, coordinate));
                if (dimensions > 1) { Gui.TextUnformatted(field.Type == typeof(Rectangle) ? SpriteRectangleAxes[axis] : SpriteVectorAxes[axis]); Gui.SameLine(); }
                Gui.SetNextItemWidth(-1);
                var initial = SpriteCoordinate(field.Node.Retrieve()!, axis);
                var range = ranges.ElementAtOrDefault(axis);
                var ranged = dimensions == 1 && NativeRangedNumericControl.Matches(field.Type, range);
                object? display = initial; var active = false; var complete = false;
                var feedback = Gui.GetID("##Value");
                try
                {
                    if (ranged)
                    {
                        using var disabled = ImGuiScopes.Disabled(!Editable());
                        complete = NativeRangedNumericControl.Draw(field.Node, type, initial, false, ranges, Gui.GetStyle().FontScaleDpi,
                            out display, out _);
                        active = Gui.IsItemActive();
                    }
                    PropertyGrid.Strings.Draw(field.Node, false, Editable, () => ReadSpriteSettingDraft(document, field), text =>
                    {
                        var parsed = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, range));
                        if (parsed is float single && !float.IsFinite(single) || parsed is double number && !double.IsFinite(number))
                            throw new FormatException("The numeric value must be finite.");
                        if (dimensions > 1 && parsed is float component) parsed = MathF.Round(component, 3);
                        CommitSpriteSettingDraft(document, field, WithSpriteCoordinate(field.Node.Retrieve(), field.Type, coordinate, parsed));
                    }, () => AcquireSpriteSettingDraft(document, field),
                        text => NativeNumericInput.StepText(type, SpriteCoordinate(field.Node.Retrieve()!, coordinate), false, range, ref text) ? text : null,
                        value => dimensions > 1 && SpriteCoordinate(value!, coordinate) is float component
                            ? MathF.Round(component, 3).ToString(CultureInfo.InvariantCulture)
                            : NativeNumericText.Format(SpriteCoordinate(value!, coordinate), type, range),
                        controlLabel: "##Value",
                        externalText: ranged && display != null && !Equals(initial, display) ? NativeNumericText.Format(display, type, range) : null,
                        externalActive: active, externalCommit: complete);
                }
                catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                PropertyConversionFeedback.Draw(feedback);
            }
            finally { Gui.PopID(); }
        }
    }
    private static object SpriteCoordinate(object value, int axis) => value switch
    {
        Vector2 vector => axis == 0 ? vector.X : vector.Y,
        Vector4 vector => axis == 0 ? vector.X : axis == 1 ? vector.Y : axis == 2 ? vector.Z : vector.W,
        Rectangle rectangle => axis == 0 ? rectangle.X : axis == 1 ? rectangle.Y : axis == 2 ? rectangle.Width : rectangle.Height,
        _ => value,
    };
}
