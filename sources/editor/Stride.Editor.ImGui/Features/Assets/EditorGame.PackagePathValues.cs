using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackagePathValue(Package package, PackageSettingAccessor accessor, Type declared, string label,
        object? value, string path, bool editable, Action<object?> set, INodePresenter? presenter)
    {
        DrawPackageSettingScalar(package, accessor, declared, label, value, path, set, presenter, editable);
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var tree = packageSettingTrees.GetValueOrDefault((package, accessor.Key));
        var source = accessor.Get(package);
        var providerVersion = history.Extensions?.Version;
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        var expected = Read();
        bool Current() => editable && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && owner.AuthoringRevision == revision
            && state is { IsPublished: true, IsDisposed: false } && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && history.Extensions?.Version == providerVersion
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && tree != null && SameSettingValue(accessor, source, tree.Source)
            && SameSettingValue(accessor, accessor.Get(package), source) && Equals(expected, Read());
        void Validate()
        {
            if (!Current()) throw new InvalidOperationException("The registered package path changed or is read-only.");
        }
        using var phase = BeginPackageReferenceCommitPhase(package, accessor);
        RegisterPropertyPathDropTarget(owner, declared, expected, Current, Read, chosen => phase.Submit(() =>
        {
            Validate();
            if (!Equals(Read(), chosen)) set(chosen);
        }));
        Gui.SameLine();
        using var disabled = ImGuiScopes.Disabled(!Current());
        if (!Gui.SmallButton("...")) return;
        Execute(() =>
        {
            Validate();
            ValidatePendingPropertyStrings();
            Validate();
            var current = Read() as UPath;
            var directory = current is UDirectory folder ? folder : current?.GetFullDirectory();
            if (directory?.IsAbsolute == false)
            {
                var sessionPath = session?.SolutionPath ?? session?.LocalPackages.FirstOrDefault()?.FullPath;
                if (sessionPath != null) directory = UPath.Combine(sessionPath.GetFullDirectory(), directory).GetFullDirectory();
            }
            var selected = declared == typeof(UDirectory)
                ? WindowsFileDialogs.OpenDirectory(Window.NativeWindow.Handle, null, directory?.ToOSPath())
                : WindowsFileDialogs.Open(Window.NativeWindow.Handle, "Open", "All files\0*.*\0\0", directory?.ToOSPath(), (current as UFile)?.ToOSPath());
            if (selected == null) return;
            Validate();
            UPath chosen = declared == typeof(UDirectory) ? new UDirectory(selected) : new UFile(selected);
            if (Equals(Read(), chosen)) return;
            phase.Submit(() => { Validate(); set(chosen); });
        });
    }
}
