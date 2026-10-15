using System.Reflection;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Original EnumValues distinguishes flag members from nonflag aliases;
    // EnumToDisplayName uses the actual enum field's DisplayAttribute only.
    private static string PackageEnumDisplay(object? value)
    {
        if (value == null) return "(None)";
        var text = value.ToString() ?? "";
        var display = value.GetType().GetMember(text).FirstOrDefault()?.GetCustomAttribute<DisplayAttribute>(false);
        return display == null ? text : display.Name ?? "";
    }

    private void DrawPackageEnumValue(Package package, PackageSettingAccessor accessor, Type declared, string label,
        object? value, Action<object?> set, INodePresenter? presenter, bool canEdit)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        var flags = declared.IsDefined(typeof(FlagsAttribute), false);
        var options = type.IsDefined(typeof(FlagsAttribute), false) ? EditorFlagEnums.GetIndividualFlags(type).ToArray()
            : Enum.GetValues(type).Cast<Enum>().Distinct().ToArray();
        var history = SessionAuthoringHistory;
        var owner = history == null ? null : GetPackageSettingsDocument(package, history);
        var state = owner == null ? null : history!.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner?.AuthoringRevision;
        var session = workspace?.Session;
        var tree = packageSettingTrees.GetValueOrDefault((package, accessor.Key));
        var source = accessor.Get(package);
        var version = history?.Extensions?.Version;
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        bool Current() => canEdit && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && owner?.AuthoringRevision == revision
            && owner?.CanSave == true
            && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
            && ReferenceEquals(state.Document, owner) && ReferenceEquals(history!.Registry.Find(owner!.Path), state)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key) && history.Extensions?.Version == version
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && tree != null && SameSettingValue(accessor, source, tree.Source)
            && SameSettingValue(accessor, accessor.Get(package), source);
        void Apply(Func<Enum, object> transform, string? commandName = null)
        {
            Execute(() =>
            {
                if (!Current()) throw new InvalidOperationException("The registered package enum changed or is read-only.");
                if (commandName != null && presenter?.Commands.SingleOrDefault(command => command.Name == commandName) is { } native)
                {
                    QueuePackageSettingCommand(package, accessor, presenter, native.Name, null);
                    return;
                }
                var current = (Enum)(Read() ?? Enum.ToObject(type, 0));
                var next = transform(current);
                if (Equals(Read(), next)) return;
                var expected = Read();
                ValidatePendingPropertyStrings();
                if (!Current() || !Equals(Read(), expected)) throw new InvalidOperationException("The registered package enum changed before assignment.");
                IDisposable? pins = null;
                try
                {
                    pins = AcquirePackagePropertyPins(package, accessor, presenter, Current);
                    current = (Enum)(Read() ?? Enum.ToObject(type, 0));
                    next = transform(current);
                    if (!Current() || !Equals(Read(), expected)) throw new InvalidOperationException("The registered package enum changed during conversion.");
                    if (!Equals(Read(), next))
                    {
                        set(next);
                        CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                    }
                }
                finally
                {
                    if (pins != null) { packageCommandCleanup.Add(pins.Dispose); PumpPackageCommandCleanup(); }
                }
            });
        }
        if (owner == null)
        {
            var text = PackageEnumDisplay(value);
            NativeStringInput.Draw(label, ref text, Hexa.NET.ImGui.ImGuiInputTextFlags.ReadOnly);
            return;
        }
        using var disabled = ImGuiScopes.Disabled(!Current());
        if (icons?.HasEnumImages(declared) == true)
        {
            if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
            if (NativeEnumImageControl.Draw(icons, (object?)presenter ?? owner!, type, value, false, options, UiScale(1), out var next))
                Apply(_ => next!);
            return;
        }
        var preview = flags ? EditorLocalization.Text("Change values...", "Button") : PackageEnumDisplay(value);
        if (!BeginNativeCombo(label, preview)) return;
        try
        {
            foreach (var option in options)
            {
                Gui.PushID(option.ToString());
                try
                {
                    if (flags)
                    {
                        var mask = EnumBits(option);
                        var selected = value is Enum actual && (EnumBits(actual) & mask) == mask;
                        if (DrawNativeCheckbox(PackageEnumDisplay(option), ref selected))
                            Apply(current => EnumFromBits(type, selected ? EnumBits(current) | mask : EnumBits(current) & ~mask));
                    }
                    else if (DrawNativeSelectable(PackageEnumDisplay(option), Equals(option, value))) Apply(_ => option);
                }
                finally { Gui.PopID(); }
            }
            if (flags && DrawPackageFlagBulkActions(presenter) is { } action)
                Apply(current => ApplyFlagBulkAction(current, action), PackageFlagCommandName(action));
        }
        finally { Gui.EndCombo(); }
    }

    private static string PackageFlagCommandName(FlagBulkAction action) => action switch
    {
        FlagBulkAction.All => "FlagEnumSelectAll",
        FlagBulkAction.None => "FlagEnumSelectNone",
        FlagBulkAction.Invert => "FlagEnumSelectInvert",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private FlagBulkAction? DrawPackageFlagBulkActions(INodePresenter? presenter)
    {
        Gui.Separator();
        FlagBulkAction? selected = null;
        foreach (var action in new[] { FlagBulkAction.All, FlagBulkAction.None, FlagBulkAction.Invert })
        {
            if (action != FlagBulkAction.All) Gui.SameLine();
            var command = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == PackageFlagCommandName(action));
            using var disabled = ImGuiScopes.Disabled(command != null && !command.CanExecute([presenter!], null));
            if (Gui.SmallButton(EditorLocalization.Text(action.ToString(), "Button"))) selected = action;
        }
        return selected;
    }
}
