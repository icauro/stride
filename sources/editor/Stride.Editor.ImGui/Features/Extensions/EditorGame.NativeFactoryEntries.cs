using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SelectNativeFactoryEntry(AssetMetadataDocument owner, IMemberNode member, AbstractNodeEntry entry,
        Func<IReadOnlyList<AbstractNodeEntry>> currentEntries, Action fallback)
        => SelectNativeFactoryEntry(owner, new NodeAccessor(member, NodeIndex.Empty), entry, currentEntries, fallback);
    private void SelectNativeFactoryEntry(AssetMetadataDocument owner, NodeAccessor accessor, AbstractNodeEntry entry,
        Func<IReadOnlyList<AbstractNodeEntry>> currentEntries, Action fallback, string commandName = "CreateNewInstance")
    {
        Execute(() =>
        {
            void ValidateTarget()
            {
                if (!NativeAbstractEntries.Contains(currentEntries(), entry) || (accessor.Node is IMemberNode member ? !owner.CanEditAssetSettingNode(member) || owner.IsExtensionPropertyReadOnly(member)
                    : accessor.Node is not Stride.Core.Assets.Quantum.IAssetObjectNode collection || (accessor.Index.IsEmpty ? !owner.CanEditAssetCollection(collection) : !owner.CanEditAssetSettingItem(collection, accessor.Index))))
                    throw new InvalidOperationException("The native factory entry is no longer available.");
            }
            ValidateTarget();
            if (commandName == "CreateNewInstance" && entry.IsMatchingValue(accessor.RetrieveValue()!)) return;
            ValidatePendingPropertyStrings();
            ValidateTarget();
            var target = owner.AcquireExtensionFactoryTarget(accessor); var transferred = false;
            try
            {
                if (target?.Presenters.Any(presenter => presenter.IsReadOnly) == true) throw new InvalidOperationException("The native property is read-only.");
                var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == commandName);
                if (command != null)
                {
                    transferred = true; StartEditorExtensionCommand(owner, target!, command, entry);
                }
                else fallback();
            }
            finally
            {
                if (!transferred && target != null)
                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        });
    }
}
