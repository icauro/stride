using System.Globalization;
using System.Runtime.CompilerServices;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class PackageDateTimeView { internal readonly uint[] Inputs = new uint[6]; }
    private readonly ConditionalWeakTable<PackageSettingsDocument, Dictionary<uint, PackageDateTimeView>> packageDateTimeViews = new();

    private void DrawPackageTimeSpanValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        TimeSpan Read() => (TimeSpan)(presenter == null ? accessor.Get(package) : presenter.Value)!;
        var tree = packageSettingTrees[(package, accessor.Key)];
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var available = Math.Max(1, Gui.GetContentRegionAvail().X
            - Enumerable.Range(0, 4).Sum(axis => Gui.CalcTextSize(NativeTimeSpanInput.ComponentLabel(axis)).X)
            - Gui.GetStyle().ItemSpacing.X * 7);
        Gui.BeginGroup();
        try
        {
            for (var axis = 0; axis < 4; axis++)
            {
                if (axis > 0) Gui.SameLine();
                Gui.PushID(axis);
                try
                {
                    var coordinate = axis;
                    var type = axis == 3 ? typeof(double) : typeof(int);
                    var range = NativeTimeSpanInput.ComponentRange(axis);
                    var feedback = Gui.GetID("##Time");
                    double ReadCoordinate() => NativeTimeSpanInput.Component(Read(), coordinate);
                    Gui.SetNextItemWidth(Math.Max(1, available * (axis == 3 ? 3 : 2) / 9));
                    DrawPackagePropertyString(package, accessor, "##Time", presenter, editable, () => ReadCoordinate(),
                        text =>
                        {
                            var value = PropertyConversionFeedback.Convert(feedback, () =>
                                Convert.ToDouble(NativeNumericText.Parse(text, type, range), CultureInfo.InvariantCulture));
                            var current = Read();
                            var next = NativeTimeSpanInput.ReplaceComponent(current, coordinate, value);
                            if (next != current) set(next);
                            CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                        }, format: value => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(coordinate == 3 ? "F7" : "F0", CultureInfo.InvariantCulture),
                        step: text => NativeNumericInput.StepText(type, ReadCoordinate(), false, range, ref text) ? text : null,
                        readValues: () => [ReadCoordinate(), Read()]);
                    PropertyConversionFeedback.Draw(feedback);
                    Gui.SameLine(); Gui.TextUnformatted(NativeTimeSpanInput.ComponentLabel(axis));
                    if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) break;
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndGroup(); }
    }

    private void DrawPackageDateTimeValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var tree = packageSettingTrees[(package, accessor.Key)];
        DateTime Read() => (DateTime)(presenter == null ? accessor.Get(package) : presenter.Value)!;
        bool Current() => editable && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && owner.CanSave && owner.AuthoringRevision == revision && state is { IsPublished: true, IsDisposed: false }
            && state.Generation == generation && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && tree.Version == (history.Extensions?.Version ?? -1)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source);
        var views = packageDateTimeViews.GetValue(owner, _ => new());
        var id = Gui.GetID("##DateTime");
        if (!views.TryGetValue(id, out var view)) views[id] = view = new();
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var width = Gui.GetContentRegionAvail().X;
        Gui.SetNextItemWidth(Math.Max(1, width - UiScale(16) - Gui.GetStyle().FramePadding.X * 2 - Gui.GetStyle().ItemSpacing.X));
        var summary = Read().ToString(CultureInfo.CurrentCulture);
        NativeStringInput.Draw("##DateTime", ref summary, Hexa.NET.ImGui.ImGuiInputTextFlags.ReadOnly);
        Gui.SameLine();
        using (ImGuiScopes.Disabled(!Current()))
            if (icons!.Button("pick-date-time", EditorLocalization.Text("Pick date and time", "ToolTip"), 16))
                Execute(() =>
                {
                    ValidatePendingPropertyStrings();
                    if (!Current()) throw new InvalidOperationException("The registered date/time changed before opening.");
                    Gui.OpenPopup("##DateTimePopup");
                });
        Gui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(Math.Max(1, width), 0), new System.Numerics.Vector2(float.MaxValue));
        if (Gui.BeginPopup("##DateTimePopup"))
        {
            try
            {
                var columnWidth = 1f;
                for (var axis = 0; axis < 6; axis++)
                {
                    if (axis % 3 != 0) Gui.SameLine();
                    Gui.PushID(axis);
                    try
                    {
                        if (axis % 3 == 0)
                            columnWidth = Math.Max(1, (Gui.GetContentRegionAvail().X
                                - Enumerable.Range(axis, 3).Sum(index => Gui.CalcTextSize(NativeDateTimeInput.ComponentLabel(index)).X)
                                - Gui.GetStyle().ItemSpacing.X * 5) / 3);
                        var coordinate = axis;
                        var type = axis == 5 ? typeof(double) : typeof(int);
                        var range = NativeDateTimeInput.ComponentRange(axis);
                        var feedback = Gui.GetID("##Coordinate"); view.Inputs[axis] = feedback;
                        double ReadCoordinate() => NativeDateTimeInput.Component(Read(), coordinate);
                        Gui.SetNextItemWidth(columnWidth);
                        DrawPackagePropertyString(package, accessor, "##Coordinate", presenter, Current(), () => ReadCoordinate(),
                            text =>
                            {
                                var value = PropertyConversionFeedback.Convert(feedback, () =>
                                    Convert.ToDouble(NativeNumericText.Parse(text, type, range), CultureInfo.InvariantCulture));
                                var current = Read();
                                var replacement = NativeDateTimeInput.ReplaceComponent(current, coordinate, value);
                                var next = new DateTime(replacement.Ticks, current.Kind);
                                if (next != current) set(next);
                                CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                            }, format: value => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(coordinate == 5 ? "F7" : "F0", CultureInfo.InvariantCulture),
                            step: text => NativeNumericInput.StepText(type, ReadCoordinate(), false, range, ref text) ? text : null,
                            readValues: () => [ReadCoordinate(), Read(), Read().Kind]);
                        PropertyConversionFeedback.Draw(feedback);
                        Gui.SameLine(); Gui.TextUnformatted(NativeDateTimeInput.ComponentLabel(axis));
                        if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) break;
                    }
                    finally { Gui.PopID(); }
                }
            }
            finally { Gui.EndPopup(); }
        }
        else for (var index = 0; index < view.Inputs.Length; index++)
            if (view.Inputs[index] != 0) { packagePropertyStrings.Abandon(owner, view.Inputs[index]); view.Inputs[index] = 0; }
    }
}
