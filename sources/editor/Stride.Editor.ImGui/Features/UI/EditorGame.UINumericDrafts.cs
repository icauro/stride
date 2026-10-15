using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUINumeric(UIDocument source, object identity, Type type, object? value, bool mixed,
        Func<bool> canEdit, Func<object?[]> read, Action<object?> commit,
        Func<PropertyNumericRange?> range, Func<IReadOnlyList<PropertyNumericRange?>> ranges,
        Func<IDisposable>? acquireTarget, Func<object?, object?>? formatValue)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        var effectiveRange = range(); var sliderRanges = ranges();
        object? display = value; var sliderActive = false; var sliderCommit = false;
        if (sliderRanges.Count != 0 && sliderRanges.All(current => NativeRangedNumericControl.Matches(type, current)))
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            sliderCommit = NativeRangedNumericControl.Draw(identity, type, value, mixed, sliderRanges, UiScale(1), out display, out _);
            sliderActive = Gui.IsItemActive();
        }
        var feedback = Gui.GetID("##Value"); Gui.SetNextItemWidth(-1);
        try
        {
            PropertyGrid.Strings.Draw(identity, mixed, Editable, read,
                text => commit(PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, range()))),
                () =>
                {
                    if (!Editable()) throw new InvalidOperationException("The numeric property owner is no longer current.");
                    var leases = new List<IDisposable>();
                    try
                    {
                        leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
                        if (acquireTarget != null) leases.Add(acquireTarget());
                        return new PropertyStringOwners(leases, [source]);
                    }
                    catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
                },
                step: text =>
                {
                    if (!Editable() || !NativeNumericInput.TryGesture(type, null, out _)) return null;
                    var stepped = text;
                    return NativeNumericInput.StepText(type, value, mixed, range(), ref stepped) ? stepped : null;
                },
                format: raw => NativeNumericText.Format(formatValue == null ? raw : formatValue(raw), type, range()),
                externalText: sliderActive || sliderCommit ? NativeNumericText.Format(display, type, effectiveRange) : null,
                externalActive: sliderActive, externalCommit: sliderCommit);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}
