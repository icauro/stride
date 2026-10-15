using Hexa.NET.ImGui;
using Stride.Animations;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class CurveEditorPanel(Action<string> reportError, Func<string, string, bool, bool> drawLiteralSelectable,
    Action validatePending, Action<CurvePointNumericTarget, bool, Func<bool>> drawNumeric,
    Func<string, string, ImGuiTreeNodeFlags, bool> drawTree) : IDisposable
{
    private readonly List<CurveBinding> bindings = [];
    private readonly List<CurveBinding> retiredBindings = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CurveBinding, BindingRowIdentity> bindingRowIdentities = new();
    private sealed class BindingRowIdentity { internal readonly string Id = Guid.NewGuid().ToString(); }
    private readonly EditorSplitterState curveSplitter = new();
    private float curveListFraction = .25f;
    private readonly HashSet<ICurveTrack> removed = [];
    private readonly HashSet<object> selected = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> pointMembership = new(ReferenceEqualityComparer.Instance);
    private ICurveTrack? active;
    private float xMin = 0, xMax = 1, yMin = -1, yMax = 1;
    private bool logX, logY, tracker = true;
    private (object Point, float Key, float Value)[]? drag;
    private NVector2 dragStart, dragDelta;
    private NVector2? boxStart;
    internal bool Visible = true;
    internal void Open(CurveBinding binding)
    {
        validatePending();
        var existing = bindings.FirstOrDefault(item => ReferenceEquals(item.Identity, binding.Identity) && ReferenceEquals(item.Context.Owner, binding.Context.Owner));
        if (existing != null) { retiredBindings.Add(binding); DrainRetiredBindings(); active = existing.Hierarchy.Track; foreach (var track in existing.Tracks) removed.Remove(track); }
        else { bindings.Add(binding); active = binding.Hierarchy.Track; }
        Visible = true; ResetSelection(); Fit(0);
    }
    private void Run(Action action) { try { validatePending(); action(); } catch (Exception error) { reportError(error.Message); } }
    private bool TryValidatePending()
    {
        try { validatePending(); return true; }
        catch (Exception error) { reportError(error.Message); return false; }
    }
    internal void Draw()
    {
        DrainRetiredBindings();
        if (!Visible) return;
        foreach (var binding in bindings.Where(item => !item.IsCurrent).ToArray())
        { if (binding.Tracks.Contains(active!)) { active = null; ResetSelection(); } foreach (var track in binding.Tracks) removed.Remove(track); retiredBindings.Add(binding); bindings.Remove(binding); }
        DrainRetiredBindings();
        Gui.SetNextWindowSize(new NVector2(800, 450), ImGuiCond.FirstUseEver);
        using (var window = ImGuiScopes.Window("Curve Editor###Curve editor", ref Visible))
        {
            if (window.DrawContents)
            {
                DrawToolbar();
                var scale = Gui.GetStyle().FontScaleDpi;
                var available = Gui.GetContentRegionAvail();
                var splitterWidth = 5 * scale;
                var extent = Math.Max(1, available.X - splitterWidth);
                var listWidth = Math.Clamp(extent * curveListFraction, 1, extent);
                using (var curves = ImGuiScopes.Child("Curves", new NVector2(listWidth, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
                {
                    if (curves.DrawContents)
                    {
                        BeginCurveKeyboard();
                        foreach (var binding in bindings)
                        {
                            Gui.PushID(binding.Context.Owner.Generation.ToString());
                            try
                            {
                                Gui.PushID(bindingRowIdentities.GetValue(binding, static _ => new()).Id);
                                try
                                {
                                    DrawCurveHierarchy(binding, binding.Hierarchy, null);
                                }
                                finally { Gui.PopID(); }
                            }
                            finally { Gui.PopID(); }
                        }
                        CompleteCurveKeyboard();
                    }
                }
                Gui.SameLine(0, 0);
                EditorSplitter.Draw("##CurveSplitter", new NVector2(splitterWidth, Math.Max(1, available.Y)), true, true,
                    curveSplitter, ref curveListFraction, listWidth / scale, extent / scale, 0, extent / scale);
                Gui.SameLine(0, 0);
                using var curve = ImGuiScopes.Child("Curve", NVector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.None);
                if (curve.DrawContents && active != null) DrawCanvas(active);
            }
        }
        if (!Visible)
        {
            try { Dispose(); }
            catch (Exception error) { Visible = true; reportError(error.Message); }
        }
    }
    private void DrawToolbar()
    {
        if (Gui.Button(EditorLocalization.Widget("Fit view to curve", "ToolTip"))) Fit(0);
        Gui.SameLine(); if (Gui.Button("Fit height")) Fit(1);
        Gui.SameLine(); if (Gui.Button("Fit width")) Fit(-1);
        Gui.SameLine(); Gui.Checkbox("Tracker", ref tracker);
        Gui.SameLine();
        if (Gui.BeginPopupContextItem("Axes"))
        {
            try { if (Gui.Checkbox("Logarithmic X", ref logX)) Fit(-1); if (Gui.Checkbox("Logarithmic Y", ref logY)) Fit(1); }
            finally { Gui.EndPopup(); }
        }
        using (ImGuiScopes.Disabled(active?.Editable != true))
        {
        if (Gui.Button(EditorLocalization.Widget("Delete selected points", "Menu"))) Run(DeleteSelected);
        Gui.SameLine(); if (Gui.Button("Clear curve")) Run(() => { active!.Delete(active.ReadPoints().Select(point => point.Value).ToArray()); ResetSelection(); });
        }
        Gui.SameLine(); if (Gui.Button("Remove curve") && active != null) Run(RemoveCurveFromView);
        if (active == null) return;
        if (active is RotationCurveTrack rotation)
        {
            using var modeChoice = ImGuiScopes.Combo("##RotationDisplayMode", rotation.Display.Mode.ToString());
            if (modeChoice.DrawContents)
                foreach (var mode in Enum.GetValues<CurveRotationDisplayMode>())
                    if (Gui.Selectable(mode.ToString(), mode == rotation.Display.Mode)) Run(() => { rotation.Display.Mode = mode; ResetSelection(); Fit(0); });
        }
        if (Gui.Button(EditorLocalization.Widget("First key", "ToolTip"))) Run(() => Navigate(int.MinValue));
        Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Previous key", "ToolTip"))) Run(() => Navigate(-1));
        Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Next key", "ToolTip"))) Run(() => Navigate(1));
        Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Last key", "ToolTip"))) Run(() => Navigate(int.MaxValue));
        var points = active.ReadPoints(); var current = points.FirstOrDefault(point => selected.Contains(point.Value));
        if (selected.Count != 1 || current == null) return;
        var track = active;
        bool CurrentSelection() => ReferenceEquals(active, track) && selected.Count == 1 && selected.Contains(current.Value);
        if (track.CaptureNumericTarget(current.Value, true) is { } keyTarget) drawNumeric(keyTarget, true, CurrentSelection);
        Gui.SameLine();
        if (track.CaptureNumericTarget(current.Value, false) is { } valueTarget) drawNumeric(valueTarget, false, CurrentSelection);
        Gui.SameLine();
        using var tangentChoice = ImGuiScopes.Combo("Tangent", current.Tangent.ToString());
        if (tangentChoice.DrawContents)
            foreach (var tangent in Enum.GetValues<AnimationKeyTangentType>())
                if (Gui.Selectable(tangent.ToString(), tangent == current.Tangent)) Run(() => active.SetTangent(selected, tangent));
    }
    private void ResetSelection() { validatePending(); selected.Clear(); drag = null; boxStart = null; dragDelta = NVector2.Zero; }
    private void RemoveCurveFromView()
    {
        validatePending();
        if (active == null) return;
        var rootBinding = bindings.FirstOrDefault(binding => ReferenceEquals(binding.Hierarchy.Track, active));
        // The native command removes only a root from CurveEditorViewModel.Curves.
        if (rootBinding == null) return;
        bindings.Remove(rootBinding); retiredBindings.Add(rootBinding);
        active = bindings.FirstOrDefault()?.Hierarchy.Track;
        ResetSelection();
    }
    private void DeleteSelected() { if (active == null) return; active.Delete(selected.ToArray()); ResetSelection(); }
    private void Navigate(int step)
    {
        validatePending();
        if (active == null) return;
        var points = active.ReadPoints().OrderBy(point => point.Key).ToArray(); if (points.Length == 0) return;
        var index = Array.FindIndex(points, point => selected.Contains(point.Value));
        if (index < 0) return;
        if (step == 1) index = Array.FindLastIndex(points, point => selected.Contains(point.Value));
        index = step == int.MinValue ? 0 : step == int.MaxValue ? points.Length - 1 : Math.Clamp(index + step, 0, points.Length - 1);
        ResetSelection(); selected.Add(points[index].Value);
    }
    private void FocusSelection()
    {
        if (active == null) return;
        if (selected.Count == 0) { Fit(0); return; }
        double x = 0, y = 0; var count = 0;
        foreach (var point in active.ReadPoints())
        {
            if (!selected.Contains(point.Value)) continue;
            var pointX = Axis(point.Key, logX); var pointY = Axis(point.Component, logY);
            if (!float.IsFinite(pointX) || !float.IsFinite(pointY)) continue;
            x += pointX; y += pointY; count++;
        }
        if (count == 0) return;
        var centerX = (float)(x / count); var centerY = (float)(y / count);
        var halfWidth = (xMax - xMin) * .5f; var halfHeight = (yMax - yMin) * .5f;
        xMin = centerX - halfWidth; xMax = centerX + halfWidth;
        yMin = centerY - halfHeight; yMax = centerY + halfHeight;
    }
    private void Fit(int axis)
    {
        if (active == null) return;
        active.PrepareSampling(); var points = active.ReadPoints();
        if (axis <= 0) SetBounds(active.SamplingKeys.Select(key => Axis(key, logX)), ref xMin, ref xMax);
        if (axis >= 0 && !SetBounds(points.Select(point => Axis(point.Component, logY)), ref yMin, ref yMax))
            SetBounds(active is RotationCurveTrack rotation
                ? Enumerable.Range(0, 101).SelectMany(index => Enumerable.Range(0, rotation.Display.Mode == CurveRotationDisplayMode.Euler ? 3 : 4)
                    .Select(channel => Axis(rotation.Display.Sample(Unaxis(xMin + (xMax - xMin) * index / 100f, logX), channel), logY)))
                : Enumerable.Range(0, 101).SelectMany(index => DisplayChannels(active)
                    .Select(channel => Axis(channel.Sample(Unaxis(xMin + (xMax - xMin) * index / 100f, logX)), logY))), ref yMin, ref yMax);
    }
    private IEnumerable<ICurveTrack> DisplayChannels(ICurveTrack track)
        => track is CompoundCurveTrack compound ? compound.Channels
            : bindings.SelectMany(binding => binding.Tracks).Where(channel => channel is not CompoundCurveTrack
                && ReferenceEquals(channel.CurveIdentity, track.CurveIdentity) && !removed.Contains(channel)).DistinctBy(channel => channel.Channel);
    private static bool SetBounds(IEnumerable<float> values, ref float min, ref float max)
    {
        var found = false; var low = float.PositiveInfinity; var high = float.NegativeInfinity;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) continue;
            low = Math.Min(low, value); high = Math.Max(high, value); found = true;
        }
        min = found ? low : 0; max = found ? high : 1;
        var margin = Math.Max(.05f, (max - min) * .1f); min -= margin; max += margin;
        return found;
    }
    private static float Axis(float value, bool logarithmic) => logarithmic ? value > 0 ? MathF.Log10(value) : float.NaN : value;
    private static float Unaxis(float value, bool logarithmic) => logarithmic ? MathF.Pow(10, value) : value;
    public void Dispose()
    {
        validatePending();
        retiredBindings.AddRange(bindings); bindings.Clear(); removed.Clear(); active = null; ResetSelection();
        pointMembership.Clear(); pointMembership.TrimExcess();
        curveKeyboardRows.Clear();
        DrainRetiredBindings();
    }
    private void DrainRetiredBindings()
    {
        List<Exception>? errors = null;
        for (var index = retiredBindings.Count - 1; index >= 0; index--)
        {
            try { retiredBindings[index].Dispose(); retiredBindings.RemoveAt(index); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors != null) throw new AggregateException(errors);
    }
}
