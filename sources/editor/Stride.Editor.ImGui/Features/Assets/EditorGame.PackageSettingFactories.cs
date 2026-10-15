using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static IReadOnlyList<AbstractNodeEntry> PackageFactoryEntries(INodePresenter? presenter, Type declared, bool allowsNull)
    {
        var entries = NativeAbstractEntries.Available(presenter != null && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied)
            ? supplied : null, ContentAssetReference.Supports(declared) ? [] : SettingItemTypes(declared), allowsNull, allowNullWithoutTypes: true);
        // The original SettingsKeyWrapper.TypedValue is NotNull. The headless
        // value root has no wrapper member, so retain that caller's native policy.
        return allowsNull ? entries : entries.Where(entry => entry is not AbstractNodeValue { Value: null }).ToArray();
    }

    private void SelectPackageFactoryEntry(Package package, PackageSettingAccessor accessor, INodePresenter? presenter,
        IReadOnlyList<AbstractNodeEntry> entries, AbstractNodeEntry entry, object? current, Action<object?> fallback, string commandName)
    {
        void ValidateSource()
        {
            if (workspace == null || SessionAuthoringHistory == null || IsAuthoringPipelineUpdating
                || !workspace.Session.Packages.Contains(package) || package.IsReadOnly || package.State < PackageState.AssetsReady
                || !package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
                || !NativeAbstractEntries.Contains(entries, entry)
                || !packageSettingTrees.TryGetValue((package, accessor.Key), out var tree)
                || tree.Version != (SessionAuthoringHistory.Extensions?.Version ?? -1)
                || !SameSettingValue(accessor, accessor.Get(package), tree.Source))
                throw new InvalidOperationException("The native setting entry is no longer available.");
            if (presenter != null && (EditorPresenterItemMetadata.IsReadOnly(presenter)
                || presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied) && !NativeAbstractEntries.Contains(supplied, entry)))
                throw new InvalidOperationException("The native setting entry changed or is read-only.");
        }
        ValidateSource();
        if (commandName == "CreateNewInstance" && entry.IsMatchingValue((presenter != null ? presenter.Value : current)!)) return;
        if (presenter != null)
        {
            if (EditorPresenterItemMetadata.IsReadOnly(presenter)) throw new InvalidOperationException("The native setting value is read-only.");
            if (presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied) && !NativeAbstractEntries.Contains(supplied, entry))
                throw new InvalidOperationException("The native setting entry changed.");
            var command = presenter.Commands.SingleOrDefault(candidate => candidate.Name == commandName);
            if (command != null)
            {
                if (!command.CanExecute([presenter], entry)) throw new InvalidOperationException("The native setting command cannot use this entry.");
                QueuePackageSettingCommand(package, accessor, presenter, commandName, entry);
                return;
            }
        }
        // The original entry owns instantiation/conversion. Canonical key setters
        // validate its resulting type and record the actual profile transition.
        ValidatePendingPropertyStrings();
        ValidateSource();
        if (commandName == "CreateNewInstance")
        {
            current = presenter != null ? presenter.Value : current;
            if (entry.IsMatchingValue(current!)) return;
        }
        fallback(entry.GenerateValue(current!));
    }
}
