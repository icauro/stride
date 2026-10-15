using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, Color4> commonInheritedColorDrafts = [];
    private void DrawInheritedCommonColor(CommonComponentProperty property, Action<Func<ComponentField, object?>>? apply = null, Action? resetContext = null)
        => DrawCommonColorValue(property.Key, property.First.Type, property.Fields.Select(member => member.Node.Retrieve()).ToArray(), create =>
        {
            if (apply != null) apply(member => create(member.Node.Retrieve()));
            else Execute(() => document!.EditInheritedScalars(property.Fields, property.Fields.Select(member => create(member.Node.Retrieve())).ToArray()));
        }, resetContext ?? (() => DrawInheritedCommonReset(property)),
            axis => NativeNumericInput.Merge(property.First.Type == typeof(Color) ? typeof(byte) : typeof(float), property.Fields.Select(field => field.AxisNumericRange(axis))),
            axis => property.Fields.All(field => !field.IsAxisReadOnly(axis)));
    private unsafe void DrawCommonColorValue(string propertyKey, Type fieldType, IReadOnlyList<object?> values,
        Action<Func<object?, object?>> apply, Action? resetContext = null,
        Func<int, PropertyNumericRange?>? axisRange = null, Func<int, bool>? axisCanEdit = null)
    {
        var value = values[0]; var mixedValues = values.Skip(1).Any(other => !Equals(other, value));
        var current = commonInheritedColorDrafts.GetValueOrDefault(propertyKey, mixedValues ? Color4.Black : value is Color packed ? packed.ToColor4()
            : value is Color3 rgb ? rgb.ToColor4() : value as Color4? ?? Color4.Black);
        if (Gui.ColorButton("##Swatch", new System.Numerics.Vector4(current.R, current.G, current.B, current.A), ImGuiColorEditFlags.NoTooltip, new System.Numerics.Vector2(44, 22))) Gui.OpenPopup("##Color");
        resetContext?.Invoke();
        Gui.SameLine();
        DrawCommonColorHexValue(propertyKey, fieldType, value, mixedValues, color => apply(_ => color));
        resetContext?.Invoke();
        if (Gui.BeginPopup("##Color"))
        {
            float* channels = stackalloc float[4] { current.R, current.G, current.B, current.A };
            var changed = false; var ended = false;
            if (Gui.BeginTabBar("ColorTabs"))
            {
                if (Gui.BeginTabItem("Color picker"))
                {
                    Gui.SetNextItemWidth(270);
                    var flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.InputRgb | ImGuiColorEditFlags.PickerHueBar;
                    changed = fieldType == typeof(Color3) ? Gui.ColorPicker3("##Picker", channels, flags) : Gui.ColorPicker4("##Picker", channels, flags | ImGuiColorEditFlags.AlphaBar);
                    ended = Gui.IsItemDeactivatedAfterEdit(); Gui.EndTabItem();
                }
                if (Gui.BeginTabItem("Palette"))
                {
                    foreach (var entry in ColorPaletteViewModel.Colors)
                    {
                        Gui.PushID(entry.Key); var color = entry.Value;
                        Gui.ColorButton("##Preview", new System.Numerics.Vector4(color.R, color.G, color.B, 1), ImGuiColorEditFlags.NoTooltip, new System.Numerics.Vector2(28, 20)); Gui.SameLine();
                        if (Gui.Selectable(entry.Key)) { channels[0] = color.R; channels[1] = color.G; channels[2] = color.B; channels[3] = 1; changed = ended = true; }
                        Gui.PopID();
                    }
                    Gui.EndTabItem();
                }
                Gui.EndTabBar();
            }
            if (changed) commonInheritedColorDrafts[propertyKey] = new Color4(channels[0], channels[1], channels[2], channels[3]);
            if (ended) CommitColor();
            Gui.EndPopup();
        }
        else CommitColor();
        for (var axis = 0; axis < (fieldType == typeof(Color3) ? 3 : 4); axis++)
        {
            var index = axis; Gui.PushID(axis); Gui.TextUnformatted("RGBA"[axis].ToString()); Gui.SameLine(); Gui.SetNextItemWidth(-1);
            var coordinate = CommonCoordinate(value, axis);
            var mixed = values.Skip(1).Any(other => !Equals(CommonCoordinate(other, axis), coordinate));
            var key = (propertyKey, axis); var text = commonPropertyDrafts.GetValueOrDefault(key) ?? (mixed ? "" : Convert.ToString(coordinate, CultureInfo.InvariantCulture) ?? "");
            var editable = axisCanEdit?.Invoke(axis) != false;
            var submitted = Gui.InputTextWithHint("##Value", mixed ? DifferentPropertyValues : "", ref text, 128,
                ImGuiInputTextFlags.EnterReturnsTrue | (editable ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
            var numericType = fieldType == typeof(Color) ? typeof(byte) : typeof(float);
            submitted |= editable && NativeNumericInput.TryGesture(numericType, null, out _)
                && NativeNumericInput.StepText(numericType, coordinate, mixed, axisRange?.Invoke(axis), ref text);
            var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
            if (active) commonPropertyDrafts[key] = text; else commonPropertyDrafts.Remove(key);
            var feedback = Gui.GetID("##Value");
            if (editable && (submitted || ended)) apply(currentValue => PropertyConversionFeedback.Convert(feedback,
                () => WithCommonCoordinate(currentValue, fieldType, index, float.Parse(text, CultureInfo.InvariantCulture))));
            PropertyConversionFeedback.Draw(feedback);
            resetContext?.Invoke(); Gui.PopID();
        }
        void CommitColor()
        {
            if (!commonInheritedColorDrafts.Remove(propertyKey, out var color)) return;
            object updated = fieldType == typeof(Color) ? (object)new Color(color.R, color.G, color.B, color.A)
                : fieldType == typeof(Color3) ? new Color3(color.R, color.G, color.B) : color;
            apply(_ => updated);
        }
    }
}
