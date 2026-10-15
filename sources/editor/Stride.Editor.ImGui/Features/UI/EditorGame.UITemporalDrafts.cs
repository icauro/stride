using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class UITemporalView { internal readonly uint[] Inputs = new uint[6]; internal bool Opened; }
    private readonly ConditionalWeakTable<object, Dictionary<uint, UITemporalView>> uiTemporalViews = new();
    private void DrawRetainedUITemporal(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount, Func<IDisposable>? acquireTarget)
    {
        var date = type == typeof(DateTime);
        object?[] Read()
        {
            var values = read();
            return date ? values.Concat(values.Take(valueCount).Select(value => value is DateTime time ? (object?)time.Kind : null)).ToArray() : values;
        }
        var initial = Read(); var mixed = initial.Take(valueCount).Any(value => !Equals(value, initial[0]));
        bool Editable() => !mixed && ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        double Component(object? value, int axis) => date ? NativeDateTimeInput.Component(value is DateTime time ? time : default, axis)
            : NativeTimeSpanInput.Component(value is TimeSpan span ? span : default, axis);
        string Label(int axis) => date ? NativeDateTimeInput.ComponentLabel(axis) : NativeTimeSpanInput.ComponentLabel(axis);
        var states = uiTemporalViews.GetValue(identity, _ => new()); var id = Gui.GetID("##DateTime");
        if (!states.TryGetValue(id, out var view)) states[id] = view = new();
        Gui.BeginGroup();
        try
        {
            if (!date)
            {
                var available = Math.Max(1, Gui.GetContentRegionAvail().X
                    - Enumerable.Range(0, 4).Sum(axis => Gui.CalcTextSize(Label(axis)).X) - Gui.GetStyle().ItemSpacing.X * 7);
                using var disabled = ImGuiScopes.Disabled(mixed);
                for (var axis = 0; axis < 4; axis++)
                {
                    if (axis > 0) Gui.SameLine(); Gui.PushID(axis);
                    try { Gui.SetNextItemWidth(Math.Max(1, available * (axis == 3 ? 3 : 2) / 9)); DrawComponent(axis); }
                    finally { Gui.PopID(); }
                }
                return;
            }
            var width = Gui.GetContentRegionAvail().X;
            Gui.SetNextItemWidth(Math.Max(1, width - UiScale(16) - Gui.GetStyle().FramePadding.X * 2 - Gui.GetStyle().ItemSpacing.X));
            var summary = mixed ? "" : (initial[0] is DateTime time ? time : default).ToString(CultureInfo.CurrentCulture);
            NativeStringInput.DrawWithHint("##DateTime", mixed ? DifferentTemporalValues : "", ref summary, ImGuiInputTextFlags.ReadOnly);
            Gui.SameLine();
            using (ImGuiScopes.Disabled(!Editable()))
                if (icons!.Button("pick-date-time", EditorLocalization.Text("Pick date and time", "ToolTip"), 16))
                {
                    ValidatePendingPropertyStrings();
                    if (!Editable()) throw new InvalidOperationException("The date/time property changed before opening.");
                    view.Opened = true; Gui.OpenPopup("##DateTimePopup");
                }
            Gui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(Math.Max(1, width), 0), new System.Numerics.Vector2(float.MaxValue));
            if (Gui.BeginPopup("##DateTimePopup"))
            {
                try
                {
                    if (!view.Opened || !Editable()) { Gui.CloseCurrentPopup(); return; }
                    var columnWidth = 1f;
                    for (var axis = 0; axis < 6; axis++)
                    {
                        if (axis % 3 != 0) Gui.SameLine(); Gui.PushID(axis);
                        try
                        {
                            if (axis % 3 == 0)
                                columnWidth = Math.Max(1, (Gui.GetContentRegionAvail().X
                                    - Enumerable.Range(axis < 3 ? 0 : 3, 3).Sum(coordinate => Gui.CalcTextSize(Label(coordinate)).X)
                                    - Gui.GetStyle().ItemSpacing.X * 5) / 3);
                            Gui.SetNextItemWidth(columnWidth); DrawComponent(axis);
                        }
                        finally { Gui.PopID(); }
                    }
                }
                finally { Gui.EndPopup(); }
            }
            else
            {
                view.Opened = false;
                foreach (var control in view.Inputs.Where(control => control != 0)) PropertyGrid.Strings.Abandon(identity, control);
                Array.Clear(view.Inputs);
            }
        }
        finally { Gui.EndGroup(); }
        void DrawComponent(int axis)
        {
            var scalarType = axis == (date ? 5 : 3) ? typeof(double) : typeof(int);
            var range = date ? NativeDateTimeInput.ComponentRange(axis) : NativeTimeSpanInput.ComponentRange(axis);
            var control = date ? "##Coordinate" : "##Time"; var feedback = Gui.GetID(control); view.Inputs[axis] = feedback;
            try
            {
                PropertyGrid.Strings.Draw(identity, mixed, Editable, Read, text =>
                {
                    var coordinate = PropertyConversionFeedback.Convert(feedback, () =>
                        Convert.ToDouble(NativeNumericText.Parse(text, scalarType, range), CultureInfo.InvariantCulture));
                    transform(current =>
                    {
                        if (!date) return current is TimeSpan span ? NativeTimeSpanInput.ReplaceComponent(span, axis, coordinate)
                            : throw new InvalidOperationException("The duration property no longer supplies a value.");
                        if (current is not DateTime time) throw new InvalidOperationException("The date/time property no longer supplies a value.");
                        var replacement = NativeDateTimeInput.ReplaceComponent(time, axis, coordinate);
                        return new DateTime(replacement.Ticks, time.Kind);
                    });
                }, () => AcquireUIValueDraftOwners(source, Editable, acquireTarget),
                    step: text => NativeNumericInput.StepText(scalarType, Component(Read()[0], axis), mixed, range, ref text) ? text : null,
                    format: value => Component(value, axis).ToString(axis == (date ? 5 : 3) ? "F7" : "F0", CultureInfo.InvariantCulture), controlLabel: control);
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
            PropertyConversionFeedback.Draw(feedback); Gui.SameLine(); Gui.TextUnformatted(Label(axis));
        }
    }
    private static string DifferentTemporalValues => EditorLocalization.Text("(Different values)");
}
