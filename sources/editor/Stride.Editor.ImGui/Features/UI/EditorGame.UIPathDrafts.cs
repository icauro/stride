using Hexa.NET.ImGui;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIPath(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<object?> commit, int valueCount, Func<IDisposable>? acquireTarget)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        var values = read(); var mixed = values.Take(valueCount).Any(value => !Equals(value, values[0]));
        var feedback = Gui.GetID("##Value");
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - UiScale(16) - Gui.GetStyle().FramePadding.X * 2 - Gui.GetStyle().ItemSpacing.X));
        UPath Parse(string text) => type == typeof(UDirectory) ? new UDirectory(text) : new UFile(text);
        try
        {
            PropertyGrid.Strings.Draw(identity, mixed, Editable, read,
                text => commit(PropertyConversionFeedback.Convert(feedback, () => Parse(text))),
                () => AcquireUIValueDraftOwners(source, Editable, acquireTarget), format: value => value?.ToString() ?? "");
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
        var baseline = read();
        bool Current() => Editable() && baseline.SequenceEqual(read());
        RegisterPropertyPathDropTarget(source, type, baseline[0], Current, () => read()[0], chosen =>
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The path property changed before the file drop could be applied.");
            using var lease = AcquireUIValueDraftOwners(source, Current, acquireTarget);
            commit(chosen);
        });
        Gui.SameLine();
        using var disabled = ImGuiScopes.Disabled(!Editable());
        if (!icons!.Button("open", type == typeof(UDirectory) ? "Browse directory" : "Browse file", 16)) return;
        var dialogOwner = GetCurrentDialogOwner();
        Execute(() =>
        {
            ValidatePendingPropertyStrings(); source.CommitEdits();
            if (!Editable()) throw new InvalidOperationException("The path property changed before browsing.");
            var expected = read();
            bool PickerCurrent() => Editable() && expected.SequenceEqual(read());
            using var lease = AcquireUIValueDraftOwners(source, PickerCurrent, acquireTarget);
            var current = expected[0] as UPath;
            var directory = current is UDirectory folder ? folder : current?.GetFullDirectory();
            if (directory != null)
            {
                var sessionPath = workspace?.Session.SolutionPath ?? workspace?.Session.LocalPackages.FirstOrDefault()?.FullPath;
                if (sessionPath != null) directory = UPath.Combine(sessionPath.GetFullDirectory(), directory).GetFullDirectory();
            }
            var selected = type == typeof(UDirectory)
                ? WindowsFileDialogs.OpenDirectory(dialogOwner, null, directory?.ToOSPath())
                : WindowsFileDialogs.Open(dialogOwner, "Open", "All files\0*.*\0\0", directory?.ToOSPath(), (current as UFile)?.ToOSPath());
            if (selected == null) return;
            if (!PickerCurrent()) throw new InvalidOperationException("The path property changed while browsing.");
            UPath chosen = Parse(selected);
            if (!expected.Take(valueCount).All(value => Equals(value, chosen))) commit(chosen);
        });
    }
}
