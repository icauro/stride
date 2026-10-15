using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawPackageAcceptableValues(Package package, PackageSettingAccessor accessor, string label, object? value,
        bool editable, bool allowsNull, Action<object?> set, INodePresenter? presenter)
    {
        var choices = accessor.Key.AcceptableValues.ToArray();
        if (choices.Length == 0) return false;
        var history = SessionAuthoringHistory;
        var source = accessor.Get(package);
        var providerVersion = history?.Extensions?.Version;
        var tree = packageSettingTrees.GetValueOrDefault((package, accessor.Key));
        using var disabled = ImGuiScopes.Disabled(!editable);
        if (BeginNativeCombo(label, value?.ToString() ?? ""))
        {
            try
            {
                for (var index = 0; index < choices.Length; index++)
                {
                    var choice = choices[index];
                    Gui.PushID(index);
                    try
                    {
                    if (DrawNativeSelectable(choice?.ToString() ?? "", Equals(value, choice))) Execute(() =>
                    {
                        void ValidateSource()
                        {
                            if (!editable || workspace == null || !ReferenceEquals(SessionAuthoringHistory, history) || history == null
                                || IsAuthoringPipelineUpdating || !workspace.Session.Packages.Contains(package) || package.IsReadOnly
                                || !package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key) || history.Extensions?.Version != providerVersion
                                || presenter != null && EditorPresenterItemMetadata.IsReadOnly(presenter)
                                || !SameSettingValue(accessor, accessor.Get(package), source)
                                || tree != null && !ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree))
                                throw new InvalidOperationException("The registered setting choice changed or is read-only.");
                        }
                        ValidateSource();
                        if (Equals(accessor.Get(package), choice)) return;
                        ValidatePendingPropertyStrings();
                        ValidateSource();
                        if (choice == null && !allowsNull) throw new ArgumentException("This registered setting does not allow null.");
                        set(choice);
                    });
                    }
                    finally { Gui.PopID(); }
                }
            }
            finally { Gui.EndCombo(); }
        }
        return true;
    }
}
