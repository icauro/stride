using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageBooleanValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var version = history.Extensions?.Version;
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        var previous = Read();
        bool Current() => editable && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && owner.CanSave && owner.AuthoringRevision == revision
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true
            && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
            && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && history.Extensions?.Version == version
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source) && Equals(Read(), previous);
        // Original single-value nullable CheckBox is not IsThreeState: an
        // indeterminate null toggles to false, then false/true alternate.
        var value = previous is bool boolean ? boolean : true;
        using var disabled = ImGuiScopes.Disabled(!Current());
        Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, previous == null);
        try
        {
            if (DrawNativeCheckbox(label.Length == 0 ? "##Boolean" : label, ref value))
            {
                var next = value;
                Execute(() =>
                {
                    if (!Current()) throw new InvalidOperationException("The registered package boolean changed or is read-only.");
                    if (Equals(previous, next)) return;
                    ValidatePendingPropertyStrings();
                    if (!Current()) throw new InvalidOperationException("The registered package boolean changed before validation.");
                    set(next);
                    CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                });
            }
        }
        finally { Gui.PopItemFlag(); }
    }
}
