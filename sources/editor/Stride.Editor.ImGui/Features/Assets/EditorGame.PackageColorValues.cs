using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class PackageColorView
    {
        internal object? Observed;
        internal ColorHSV Hsv;
    }
    private readonly ConditionalWeakTable<PackageSettingsDocument, Dictionary<uint, PackageColorView>> packageColorViews = new();
    private static bool IsPackageColorType(Type type) => type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4);
    private static Color4 PackageColor(object? value) => value is Color packed ? packed.ToColor4()
        : value is Color3 rgb ? rgb.ToColor4() : (Color4)value!;
    private static object PackageColorValue(Type type, Color4 value, bool quantize)
    {
        var packed = new Color(value.ToArray());
        if (type == typeof(Color)) return packed;
        if (quantize) value = packed.ToColor4();
        return type == typeof(Color3) ? (object)value.ToColor3() : value;
    }
    private void DrawPackageColorValue(Package package, PackageSettingAccessor accessor, Type type, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var tree = packageSettingTrees[(package, accessor.Key)];
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        var observed = Read();
        var views = packageColorViews.GetValue(owner, _ => new());
        var viewId = Gui.GetID("##PackageColor");
        if (!views.TryGetValue(viewId, out var view)) views[viewId] = view = new();
        if (!Equals(view.Observed, observed))
        {
            view.Observed = observed;
            view.Hsv = ColorHSV.FromColor(new Color(PackageColor(observed).ToArray()).ToColor4());
        }
        bool Current() => editable && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && owner.AuthoringRevision == revision
            && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
            && owner.CanSave && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && tree.Version == (history.Extensions?.Version ?? -1)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source) && Equals(Read(), observed);
        void Set(object next)
        {
            if (!Current()) throw new InvalidOperationException("The registered package color changed or is read-only.");
            if (Equals(Read(), next)) return;
            set(next);
            CommitPackageSettingEdits(package, accessor, retainPresentation: true);
        }
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var visual = new Color(PackageColor(observed).ToArray()).ToColor4();
        using (ImGuiScopes.Disabled(!Current()))
            if (Gui.ColorButton("##Swatch", new NVector4(visual.R, visual.G, visual.B, visual.A), ImGuiColorEditFlags.NoTooltip, new NVector2(UiScale(22))))
                Execute(() =>
                {
                    ValidatePendingPropertyStrings();
                    if (!Current()) throw new InvalidOperationException("The registered package color changed before opening.");
                    Gui.OpenPopup("##PackageColorPopup");
                });

        string? liveText = null;
        var liveActive = false;
        var liveCommit = false;
        Gui.SetNextWindowSizeConstraints(new NVector2(UiScale(200)), new NVector2(UiScale(400)));
        if (Gui.BeginPopup("##PackageColorPopup"))
        {
            try
            {
                using var disabled = ImGuiScopes.Disabled(!Current());
                if (Gui.BeginTabBar("##ColorTabs"))
                {
                    try
                    {
                        if (Gui.BeginTabItem(EditorLocalization.Text("Color picker")))
                        {
                            try
                            {
                                var color = new Color(view.Hsv.ToColor().ToArray()).ToColor4();
                                var display = new NVector4(color.R, color.G, color.B, color.A);
                                var flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.Float
                                    | ImGuiColorEditFlags.InputRgb | ImGuiColorEditFlags.PickerHueBar;
                                Gui.SetNextItemWidth(UiScale(270));
                                bool changed;
                                if (type == typeof(Color3))
                                {
                                    var rgb = new NVector3(display.X, display.Y, display.Z);
                                    changed = Gui.ColorPicker3("##Picker", ref rgb, flags);
                                    display = new(rgb.X, rgb.Y, rgb.Z, 1);
                                }
                                else changed = Gui.ColorPicker4("##Picker", ref display, flags | ImGuiColorEditFlags.AlphaBar);
                                liveActive = Gui.IsItemActive(); liveCommit = Gui.IsItemDeactivatedAfterEdit();
                                if (changed)
                                {
                                    var packed = new Color(display.X, display.Y, display.Z, display.W).ToColor4();
                                    view.Hsv = ColorHSV.FromColor(packed);
                                }
                                if (liveActive || liveCommit)
                                    liveText = ColorHex(PackageColorValue(type, view.Hsv.ToColor(), quantize: true));
                                DrawPackageColorNumbers(package, accessor, type, presenter, Current(), Read, Set, view);
                                if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) Gui.CloseCurrentPopup();
                            }
                            finally { Gui.EndTabItem(); }
                        }
                        if (SameSettingValue(accessor, accessor.Get(package), tree.Source)
                            && Gui.BeginTabItem(EditorLocalization.Text("Palette")))
                        {
                            try
                            {
                                foreach (var entry in ColorPaletteViewModel.Colors)
                                {
                                    Gui.PushID(entry.Key);
                                    try
                                    {
                                        var color = entry.Value.ToColor4();
                                        var swatch = new Color(color.ToArray()).ToColor4();
                                        Gui.ColorButton("##Preview", new NVector4(swatch.R, swatch.G, swatch.B, 1), ImGuiColorEditFlags.NoTooltip, new NVector2(28, 20) * UiScale(1));
                                        Gui.SameLine();
                                        if (DrawNativeSelectable(entry.Key)) Execute(() =>
                                        {
                                            if (!Current()) throw new InvalidOperationException("The registered package color changed.");
                                            ValidatePendingPropertyStrings();
                                            Set(PackageColorValue(type, color, quantize: false));
                                        });
                                    }
                                    finally { Gui.PopID(); }
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
        if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) return;
        Gui.SameLine(); Gui.SetNextItemWidth(-1);
        DrawPackagePropertyString(package, accessor, "##Hex", presenter, editable, Read,
            text => Set(ParseColorHex(text, type)), format: ColorHex,
            externalText: liveText, externalActive: liveActive, externalCommit: liveCommit);
    }

    private void DrawPackageColorNumbers(Package package, PackageSettingAccessor accessor, Type type, INodePresenter? presenter,
        bool editable, Func<object?> read, Action<object> set, PackageColorView view)
    {
        var color = new Color(view.Hsv.ToColor().ToArray());
        var tree = packageSettingTrees[(package, accessor.Key)];
        Gui.TextUnformatted("RGBA");
        for (var channel = 0; channel < (type == typeof(Color3) ? 3 : 4); channel++)
        {
            Gui.PushID(channel);
            try
            {
                var axis = channel;
                double ReadChannel() => axis switch { 0 => color.R, 1 => color.G, 2 => color.B, _ => color.A };
                var range = new PropertyNumericRange(new PropertyNumericMetadata(0, 255, 1, 10, 0), typeof(double));
                var channelLabel = EditorLocalization.Text("RGBA"[axis] + ":", "RGBA");
                var feedback = Gui.GetID(channelLabel);
                DrawPackagePropertyString(package, accessor, channelLabel, presenter, editable,
                    () => ReadChannel(), text =>
                    {
                        var number = PropertyConversionFeedback.Convert(feedback, () => (byte)PackageColorNumber(text, range));
                        var next = color;
                        if (axis == 0) next.R = number; else if (axis == 1) next.G = number; else if (axis == 2) next.B = number; else next.A = number;
                        set(PackageColorValue(type, next.ToColor4(), quantize: true));
                        view.Hsv = ColorHSV.FromColor(next.ToColor4());
                    }, step: text => NativeNumericInput.StepText(typeof(double), ReadChannel(), false, range, ref text) ? text : null,
                    readValues: () => [ReadChannel(), read()]);
                PropertyConversionFeedback.Draw(feedback);
                if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) return;
            }
            finally { Gui.PopID(); }
        }
        Gui.TextUnformatted("HSB");
        for (var channel = 0; channel < 3; channel++)
        {
            Gui.PushID("HSB" + channel);
            try
            {
                var axis = channel;
                double ReadChannel() => axis == 0 ? view.Hsv.H : axis == 1 ? view.Hsv.S * 100 : view.Hsv.V * 100;
                var range = new PropertyNumericRange(new PropertyNumericMetadata(0, axis == 0 ? 360 : 100, 1, 10, 0), typeof(double));
                var channelLabel = EditorLocalization.Text("HSB"[axis] + ":", "HSB");
                var feedback = Gui.GetID(channelLabel);
                DrawPackagePropertyString(package, accessor, channelLabel, presenter, editable,
                    () => ReadChannel(), text =>
                    {
                        var number = PropertyConversionFeedback.Convert(feedback, () => (float)PackageColorNumber(text, range));
                        var next = view.Hsv;
                        if (axis == 0) next.H = (number + 360) % 360; else if (axis == 1) next.S = number / 100; else next.V = number / 100;
                        set(PackageColorValue(type, next.ToColor(), quantize: true));
                        view.Hsv = next;
                    }, step: text => NativeNumericInput.StepText(typeof(double), ReadChannel(), false, range, ref text) ? text : null,
                    readValues: () => [ReadChannel(), read()]);
                PropertyConversionFeedback.Draw(feedback);
                if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) return;
            }
            finally { Gui.PopID(); }
        }
    }
    private static double PackageColorNumber(string text, PropertyNumericRange range)
    {
        var value = (double)NativeNumericText.Parse(text, typeof(double), range);
        if (!double.IsFinite(value)) throw new FormatException("The color channel must be finite.");
        return (double)range.Normalize(value)!;
    }
}
