using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class RetainedColorView { internal object? Observed; internal bool Mixed, PointerAccepted, PopupOpen; internal int Frame = -1; internal ColorHSV Hsv; }
    private readonly ConditionalWeakTable<object, Dictionary<uint, RetainedColorView>> retainedColorViews = new();
    private void DrawRetainedColor(object identity, Type type, Func<object?[]> read, Func<bool> editable,
        Action<Func<object?, object?>> apply, Func<IDisposable> acquire, Func<int, bool>? axisEditable = null, Action? reset = null,
        Action<string>? capture = null, int valueCount = 1)
    {
        var permission = editable;
        editable = () => !IsAuthoringPipelineUpdating && permission();
        var values = read(); var value = values[0];
        // Callers put all actual color values first; path certificates follow separately.
        var mixed = values.Take(valueCount).Any(other => !Equals(other, value));
        var states = retainedColorViews.GetValue(identity, _ => new()); var viewId = Gui.GetID("##Color");
        if (!states.TryGetValue(viewId, out var view)) states.Add(viewId, view = new());
        if (!Equals(view.Observed, value) || view.Mixed != mixed)
        { view.Observed = value; view.Mixed = mixed; view.Hsv = ColorHSV.FromColor(mixed || value == null ? Color4.Black : new Color(PackageColor(value).ToArray()).ToColor4()); }
        var visual = new Color(view.Hsv.ToColor().ToArray()).ToColor4();
        using (ImGuiScopes.Disabled(!editable()))
            if (Gui.ColorButton("##Swatch", new NVector4(visual.R, visual.G, visual.B, visual.A), ImGuiColorEditFlags.NoTooltip, new NVector2(UiScale(22))))
                Execute(() => { ValidatePendingPropertyStrings(); if (!editable()) throw new InvalidOperationException("The color owner changed."); Gui.OpenPopup("##Color"); });
        capture?.Invoke("swatch"); reset?.Invoke();
        string? liveText = null; var liveActive = false; var liveCommit = false;
        Gui.SetNextWindowSizeConstraints(new NVector2(UiScale(200)), new NVector2(UiScale(400)));
        var popupOpen = Gui.BeginPopup("##Color");
        if (popupOpen)
        {
            try
            {
                if (Gui.BeginTabBar("ColorTabs"))
                {
                    try
                    {
                        if (Gui.BeginTabItem(EditorLocalization.Text("Color picker")))
                        {
                            try
                            {
                                if (Gui.BeginTable("##PickerLayout", 2, ImGuiTableFlags.SizingFixedFit))
                                {
                                    try
                                    {
                                        Gui.TableSetupColumn("##Surface", ImGuiTableColumnFlags.WidthFixed, UiScale(164));
                                        Gui.TableSetupColumn("##Numbers", ImGuiTableColumnFlags.WidthFixed, UiScale(120));
                                        Gui.TableNextColumn();
                                        using (ImGuiScopes.Disabled(!editable()))
                                            NativeColorPickerSurface.Draw(ref view.Hsv, ref view.PointerAccepted, UiScale(1),
                                                () => Services.Commands.TryExecute(() => { ValidatePendingPropertyStrings(); if (!editable()) throw new InvalidOperationException("The color owner changed."); }),
                                                out liveActive, out liveCommit);
                                        if (liveActive || liveCommit) liveText = ColorHex(PackageColorValue(type, view.Hsv.ToColor(), quantize: true));
                                        capture?.Invoke("picker");
                                        Gui.TableNextColumn();
                                        Gui.TextUnformatted("RGBA");
                                        for (var axis = 0; axis < (type == typeof(Color3) ? 3 : 4); axis++) Number(axis, false);
                                        Gui.TextUnformatted("HSB");
                                        for (var axis = 0; axis < 3; axis++) Number(axis, true);
                                    }
                                    finally { Gui.EndTable(); }
                                }
                            }
                            finally { Gui.EndTabItem(); }
                        }
                        if (Gui.BeginTabItem(EditorLocalization.Text("Palette")))
                        {
                            try
                            {
                                using var disabled = ImGuiScopes.Disabled(!editable());
                                foreach (var entry in ColorPaletteViewModel.Colors)
                                {
                                    Gui.PushID(entry.Key);
                                    var color = entry.Value.ToColor4();
                                    Gui.ColorButton("##Preview", new NVector4(color.R, color.G, color.B, 1), ImGuiColorEditFlags.NoTooltip, new NVector2(28, 20) * UiScale(1)); Gui.SameLine();
                                    if (DrawNativeSelectable(entry.Key)) Execute(() => { ValidatePendingPropertyStrings(); if (!editable()) throw new InvalidOperationException("The color owner changed."); apply(_ => PackageColorValue(type, color, quantize: false)); });
                                    Gui.PopID();
                                }
                            }
                            finally { Gui.EndTabItem(); }
                        }
                    }
                    finally { Gui.EndTabBar(); }
                }
            }
            finally { Gui.EndPopup(); }
        }
        else if (view.PopupOpen && view.Frame == Gui.GetFrameCount() - 1)
            Execute(() => PropertyGrid.Strings.ValidatePendingForIdentity(identity));
        view.PopupOpen = popupOpen; view.Frame = Gui.GetFrameCount();
        Gui.SameLine(); Gui.SetNextItemWidth(-1); var feedback = Gui.GetID("##Hex");
        try
        {
            PropertyGrid.Strings.Draw(identity, mixed, editable, read, text => apply(_ => ParseColorHex(text, type)), acquire,
                format: ColorHex, controlLabel: "##Hex", externalText: liveText, externalActive: liveActive, externalCommit: liveCommit);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        capture?.Invoke("hex"); PropertyConversionFeedback.Draw(feedback); reset?.Invoke();

        void Number(int axis, bool hsv)
        {
            Gui.PushID(hsv ? "HSB" : "RGBA"); Gui.PushID(axis);
            try
            {
                var label = hsv ? new[] { "H:", "S:", "B:" }[axis] : "RGBA"[axis] + ":";
                var range = new PropertyNumericRange(new PropertyNumericMetadata(0, hsv ? axis == 0 ? 360 : 100 : 255, 1, 10, 0), typeof(double));
                double Coordinate(object? raw)
                {
                    var color = raw == null ? Color4.Black : new Color(PackageColor(raw).ToArray()).ToColor4();
                    if (hsv) { var converted = ColorHSV.FromColor(color); return axis == 0 ? converted.H : axis == 1 ? converted.S * 100 : converted.V * 100; }
                    var packed = new Color(color.ToArray()); return axis == 0 ? packed.R : axis == 1 ? packed.G : axis == 2 ? packed.B : packed.A;
                }
                object Next(object? raw, double number)
                {
                    var color = raw == null ? Color4.Black : PackageColor(raw);
                            if (hsv)
                            {
                                var state = ColorHSV.FromColor(new Color(color.ToArray()).ToColor4());
                                if (axis == 0) state.H = (float)number; else if (axis == 1) state.S = (float)number / 100; else state.V = (float)number / 100;
                                return PackageColorValue(type, state.ToColor(), quantize: true);
                            }
                            var packed = new Color(color.ToArray()); var channel = (byte)number;
                            if (axis == 0) packed.R = channel; else if (axis == 1) packed.G = channel; else if (axis == 2) packed.B = channel; else packed.A = channel;
                            return PackageColorValue(type, packed.ToColor4(), quantize: true);
                }
                bool CanEdit() => editable() && (hsv ? Enumerable.Range(0, 3).All(index => axisEditable?.Invoke(index) != false) : axisEditable?.Invoke(axis) != false);
                Gui.TextUnformatted(label); Gui.SameLine();
                var id = Gui.GetID("##Value"); var numbers = read().Take(valueCount).Select(Coordinate).ToArray();
                var start = Gui.GetCursorScreenPos(); var textWidth = Math.Max(UiScale(12), Gui.GetContentRegionAvail().X - UiScale(16));
                NativeColorNumberMouse.Draw(identity, id, () => Coordinate(read()[0]), range, CanEdit(), ImGuiP.GetActiveID() == id,
                    UiScale(1), start, textWidth, Gui.GetFrameHeight(),
                    () => Services.Commands.TryExecute(() => { ValidatePendingPropertyStrings(); if (!CanEdit()) throw new InvalidOperationException("The color owner changed."); }),
                    out var mouseText, out var mouseActive, out var mouseCommit);
                if (mouseActive && mouseText != null) view.Hsv = ColorHSV.FromColor(PackageColor(Next(read()[0], PackageColorNumber(mouseText, range))));
                Gui.SetNextItemWidth(textWidth);
                try
                {
                    PropertyGrid.Strings.Draw(identity, numbers.Skip(1).Any(number => number != numbers[0]), CanEdit, read, text =>
                    {
                        var number = PropertyConversionFeedback.Convert(id, () => PackageColorNumber(text, range));
                        apply(raw => Next(raw, number));
                    }, acquire, text => NativeNumericInput.StepText(typeof(double), Coordinate(read()[0]), mixed, range, ref text) ? text : null,
                        raw => Coordinate(raw).ToString(CultureInfo.InvariantCulture), controlLabel: "##Value",
                        externalText: mouseText, externalActive: mouseActive, externalCommit: mouseCommit);
                }
                catch (Exception error) { PropertyConversionFeedback.Fail(id, error); statusMessage = error.Message; }
                NativeColorNumberMouse.DrawAdorner(identity, id, icons, UiScale(1));
                var barStart = new NVector2(Gui.GetItemRectMin().X, Gui.GetItemRectMax().Y);
                var maximum = hsv ? axis == 0 ? 360 : 100 : 255;
                var barValue = mouseActive && mouseText != null ? PackageColorNumber(mouseText, range) : Coordinate(read()[0]);
                var brush = hsv || axis == 3 ? 0xffbcbcbcu : axis == 0 ? 0xff4e4edcu : axis == 1 ? 0xff6ac365u : 0xffd1743fu;
                Gui.GetWindowDrawList().AddRectFilled(barStart, barStart + new NVector2(textWidth * (float)Math.Clamp(barValue / maximum, 0, 1), UiScale(3)), brush);
                var nextRow = Gui.GetCursorScreenPos(); Gui.SetCursorScreenPos(nextRow + new NVector2(0, UiScale(3)));
                PropertyConversionFeedback.Draw(id); capture?.Invoke("channel/" + (hsv ? "HSB" : "RGBA")[axis]);
            }
            finally { Gui.PopID(); Gui.PopID(); }
        }
    }
}
