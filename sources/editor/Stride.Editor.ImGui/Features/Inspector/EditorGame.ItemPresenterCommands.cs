using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool HasDedicatedItemCommand(string name) => name is "CopyProperty" or "PasteProperty" or "ReplaceProperty"
        or "ResetOverride" or "RenameStringKey" or "AddNewItem" or "AddPrimitiveKey" or "RemoveItem";
    private void DrawHierarchyItemPresenterCommands(IReadOnlyList<EntityHierarchyDocument> sourceOwners,
        IReadOnlyList<IHierarchyCollectionValue> sourceItems)
    {
        var owners = sourceOwners.ToArray(); var items = sourceItems.ToArray();
        if (items.Length == 0 || owners.Length != items.Length) return;
        bool Current() => !IsAuthoringPipelineUpdating && sessionHistory is { } history
            && items.Select((item, index) => owners[index].CanCopyPropertyItem(item)
                && item.Field.Component.Entity is { } entity && owners[index].IsSelected(entity.Id)
                && history.Registry.Find(owners[index].Asset.Id) is { IsPublished: true, IsDisposed: false } state
                && ReferenceEquals(state.Document, owners[index])).All(current => current);
        DrawHierarchyPresenterCommands(owners, Current, () => AcquireHierarchyFactoryTargets(items.Length,
            index => owners[index].AcquireHierarchyItemTarget(items[index].Collection, items[index].Index)));
    }
    private void DrawHierarchyPresenterCommands(IReadOnlyList<EntityHierarchyDocument> owners, Func<bool> currentTarget,
        Func<EditorPresenterTarget?[]> acquire)
    {
        if (owners.Count == 0) return;
        DrawNativePresenterCommands(owners[0], currentTarget, acquire,
            () => new EditorHierarchyCommandsMutation(sessionHistory!, owners.Distinct().ToArray()));
    }
    private void DrawNativePresenterCommands(IAssetTagDocument owner, Func<bool> currentTarget,
        Func<EditorPresenterTarget?[]> acquire, Func<IEditorCommandMutation> createMutation)
    {
        bool Current() => !IsAuthoringPipelineUpdating && currentTarget();
        if (!Current()) return;
        var targets = acquire();
        EditorPresenterTarget? combined = null;
        try
        {
            if (targets.Any(target => target == null)) return;
            combined = new(targets.Select(target => target!).ToArray());
            var nodes = combined.Presenters;
            var choices = NativeAbstractEntries.Combine(nodes.Select(node =>
                node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries.ToArray() : []).ToArray());
            foreach (var command in NodePresenterCombination.Commands(nodes))
            {
                if (HasDedicatedItemCommand(command.Name)) continue;
                var parameters = choices.Where(entry => command.CanExecute(nodes, entry)).ToArray();
                if (parameters.Length != 0)
                {
                    if (!BeginNativeMenu(command.Name, Current())) continue;
                    try { NativeAbstractEntryControl.Choices(parameters, nodes[0].Value, entry => Execute(() => Run(command, entry))); }
                    finally { Gui.EndMenu(); }
                }
                else if (DrawNativeMenuItem(command.Name, "", false, Current() && command.CanExecute(nodes, null)))
                    Execute(() => Run(command, null));
            }
        }
        finally
        {
            if (combined != null) ReleaseHierarchyFactoryTarget(combined);
            else foreach (var target in targets) if (target != null) ReleaseHierarchyFactoryTarget(target);
        }
        void Run(INodePresenterCommand command, object? parameter)
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The indexed property command target changed.");
            var current = acquire();
            EditorPresenterTarget? target = null; var transferred = false;
            try
            {
                if (current.Any(item => item == null)) throw new InvalidOperationException("An indexed property presenter is unavailable.");
                target = new(current.Select(item => item!).ToArray());
                var actual = NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(candidate => ReferenceEquals(candidate, command));
                if (actual == null || !actual.CanExecute(target.Presenters, parameter))
                    throw new InvalidOperationException("The indexed property command is no longer available.");
                StartEditorExtensionCommand(owner, target, actual, parameter, createMutation);
                transferred = true;
            }
            finally
            {
                if (!transferred)
                {
                    if (target != null) ReleaseHierarchyFactoryTarget(target);
                    else foreach (var item in current) if (item != null) ReleaseHierarchyFactoryTarget(item);
                }
            }
        }
    }
}
