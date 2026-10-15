using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawHierarchyItemFactory(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> items)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        var entries = NativeAbstractEntries.Combine(items.Select((item, index) => owners[index].HierarchyItemEntries(item)).ToArray());
        var mixed = NativeAbstractEntryControl.IsMixed(entries, items.Select(item => item.Value).ToArray());
        using var disabled = ImGuiScopes.Disabled(items.Where((item, index) => !owners[index].CanSelectHierarchyItemEntry(item)).Any());
        Gui.SetNextItemWidth(-1);
        if (entries.Count != 0 && Gui.BeginCombo("##Type", mixed ? DifferentPropertyValues : NativeAbstractEntryControl.Label(entries, items[0].Value)))
        {
            NativeAbstractEntryControl.Choices(entries, mixed ? null : items[0].Value, entry => pendingCollectionAction = () =>
                SelectHierarchyItemEntries(owners, items, entry, () => ApplyAggregateMutation(property, field =>
                {
                    var index = AggregateFieldIndex(property, field);
                    return owners[index].PrepareHierarchyItemEntry(items[index], entry);
                }, "Edit item: " + property.First.Label)));
            Gui.EndCombo();
        }
    }
    private void SelectHierarchyItemEntries(IReadOnlyList<EntityHierarchyDocument> owners, IReadOnlyList<IHierarchyCollectionValue> items,
        AbstractNodeEntry entry, Action fallback)
        => Execute(() =>
        {
            if (owners.Count == 0 || owners.Count != items.Count || items.Where((item, index) => !owners[index].CanSelectHierarchyItemEntry(item)
                    || !NativeAbstractEntries.Contains(owners[index].HierarchyItemEntries(item), entry)).Any())
                throw new InvalidOperationException("The selected native item entry changed.");
            RunHierarchyFactoryCommand(owners, AcquireHierarchyFactoryTargets(items.Count, index => owners[index].AcquireHierarchyItemTarget(items[index].Collection, items[index].Index)),
                "CreateNewInstance", entry, fallback);
        });
    private void SelectHierarchyCollectionEntries(IReadOnlyList<EntityHierarchyDocument> owners, IReadOnlyList<HierarchyCollectionContext> contexts,
        AbstractNodeEntry entry, Action fallback)
        => Execute(() =>
        {
            if (owners.Count == 0 || owners.Count != contexts.Count || contexts.Where((context, index) => context.Origin.Component.Entity is not { } entity
                    || !owners[index].IsSelected(entity.Id) || !owners[index].CanAddNestedHierarchyCollection(context)
                    || !NativeAbstractEntries.Contains(owners[index].HierarchyCollectionEntries(context), entry)).Any())
                throw new InvalidOperationException("The selected native collection entry changed.");
            // Original dictionary commands take an actual key, rather than an AbstractNodeEntry.
            if (contexts.Any(context => context.Descriptor is DictionaryDescriptor)) { fallback(); return; }
            RunHierarchyFactoryCommand(owners, AcquireHierarchyFactoryTargets(contexts.Count, index => owners[index].AcquireHierarchyItemTarget(contexts[index].Node, NodeIndex.Empty, true)),
                "AddNewItem", entry, fallback);
        });
    private void SelectHierarchyDefaultCollectionAdd(IReadOnlyList<EntityHierarchyDocument> owners,
        IReadOnlyList<HierarchyCollectionContext> contexts, Action fallback)
        => Execute(() =>
        {
            if (owners.Count == 0 || owners.Count != contexts.Count || contexts.Where((context, index) =>
                context.Origin.Component.Entity is not { } entity || !owners[index].IsSelected(entity.Id)
                || !owners[index].CanAddNestedHierarchyCollection(context) || context.Descriptor is not CollectionDescriptor
                || owners[index].HierarchyCollectionNativeEntries(context) != null).Any())
                throw new InvalidOperationException("The selected default collection command changed.");
            RunHierarchyFactoryCommand(owners,
                AcquireHierarchyFactoryTargets(contexts.Count, index => owners[index].AcquireHierarchyItemTarget(contexts[index].Node, NodeIndex.Empty, true)),
                "AddNewItem", null, fallback);
        });
    private void SelectHierarchyDictionaryDefaultAdd(IReadOnlyList<EntityHierarchyDocument> owners,
        IReadOnlyList<HierarchyCollectionContext> contexts, object? parameter, Action fallback)

        {
            if (owners.Count == 0 || owners.Count != contexts.Count || contexts.Where((context, index) =>
                context.Origin.Component.Entity is not { } entity || !owners[index].IsSelected(entity.Id)
                || !owners[index].CanAddNestedHierarchyKeys(context)).Any())
                throw new InvalidOperationException("The selected dictionary command changed.");
            RunHierarchyFactoryCommand(owners,
                AcquireHierarchyFactoryTargets(contexts.Count, index => owners[index].AcquireHierarchyItemTarget(contexts[index].Node, NodeIndex.Empty, true)),
                "AddPrimitiveKey", parameter, fallback);
        }
    private EditorPresenterTarget?[] AcquireHierarchyFactoryTargets(int count, Func<int, EditorPresenterTarget?> acquire)
    {
        var targets = new List<EditorPresenterTarget?>();
        try { for (var index = 0; index < count; index++) targets.Add(acquire(index)); return targets.ToArray(); }
        catch
        {
            foreach (var target in targets) if (target != null) packageCommandCleanup.Add(target.Dispose);
            throw;
        }
    }
    private void RunHierarchyFactoryCommand(IReadOnlyList<EntityHierarchyDocument> owners, IReadOnlyList<EditorPresenterTarget?> targets,
        string commandName, object? entry, Action fallback)
    {
        EditorPresenterTarget? combined = null; var transferred = false;
        try
        {
            ValidatePendingPropertyStrings();
            combined = new(targets.Where(target => target != null).Select(target => target!).ToArray());
            if (targets.Where(target => target != null).SelectMany(target => target!.Presenters).Any(presenter => presenter.IsReadOnly))
                throw new InvalidOperationException("A selected native collection property is read-only.");
            if (targets.All(target => target != null))
            {
                var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == commandName);
                if (command != null)
                {
                    var history = sessionHistory ?? throw new InvalidOperationException("The selected properties have no canonical history.");
                    StartEditorExtensionCommand(owners[0], combined, command, entry,
                        () => new EditorHierarchyCommandsMutation(history, owners));
                    transferred = true; return;
                }
            }
            else if (targets.Where(target => target != null).SelectMany(target => target!.Presenters).Any(presenter => presenter.Commands.Any(command => command.Name == commandName)))
                throw new InvalidOperationException("A selected native factory presenter is no longer available.");
            fallback();
        }
        finally
        {
            if (!transferred)
            {
                if (combined != null) ReleaseHierarchyFactoryTarget(combined);
                else foreach (var target in targets) if (target != null) ReleaseHierarchyFactoryTarget(target);
            }
        }
    }
    private void ReleaseHierarchyFactoryTarget(EditorPresenterTarget target)
    {
        try { target.Dispose(); }
        catch { packageCommandCleanup.Add(target.Dispose); throw; }
    }
}
