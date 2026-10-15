using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void QueueNestedAggregateMutation(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionContext> contexts,
        Func<EntityHierarchyDocument, HierarchyCollectionContext, int, Action> prepare, string title, bool requireCollectionEdit = true)
    {
        var seen = new HashSet<IAssetObjectNode>(ReferenceEqualityComparer.Instance);
        QueueAggregateMutation(property, field =>
        {
            var index = AggregateFieldIndex(property, field); var context = contexts[index]; var owner = aggregatePropertyOwners[field];
            if (!owner.CanReadNestedHierarchyCollection(context) || context.Origin.Component.Entity is not { } entity || !owner.IsSelected(entity.Id)
                || (requireCollectionEdit ? !owner.CanEditNestedHierarchyCollection(context) : !owner.CanEditHierarchyField(context.Origin)))
                throw new InvalidOperationException("The nested collection owner changed.");
            var change = prepare(owner, context, index);
            return seen.Add(context.Node) ? change : () => { };
        }, title);
    }
    private void DrawAggregateHierarchyCollection(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionContext> contexts)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        var editable = contexts.Select((context, index) => owners[index].CanEditNestedHierarchyCollection(context)).All(value => value);
        Gui.PushID(contexts[0].Key);
        var open = Gui.TreeNodeEx("##Collection", ImGuiTreeNodeFlags.None);
        DrawNestedHierarchyReset(property, contexts);
        if (contexts[0].Parent == null) { Gui.SameLine(); DrawAggregateTopologyLabel(property); }
        if (open)
        {
            var rows = contexts.Select((context, index) => owners[index].GetNestedHierarchyItems(context)).ToArray();
            foreach (var first in rows[0])
            {
                var combineKey = first.Index.ToString();
                if (rows[0].Count(item => item.Index.ToString() == combineKey) != 1) continue;
                var items = new List<HierarchyCollectionItem> { first };
                foreach (var set in rows.Skip(1))
                {
                    var matches = set.Where(item => item.Index.ToString() == combineKey && item.DeclaredType == first.DeclaredType).ToArray();
                    if (matches.Length != 1) break; items.Add(matches[0]);
                }
                if (items.Count != contexts.Count) continue;
                if (items.Select((item, index) => owners[index].IsHierarchyItemVisible(item)).All(visible => !visible)) continue;
                Gui.PushID(first.IdentityKey);
                var canEditItem = editable && items.Select((item, index) => owners[index].CanEditHierarchyItem(item.Collection, item.Index)).All(value => value);
                if (contexts[0].Descriptor is DictionaryDescriptor)
                    DrawAggregateDictionaryKey(property, items);
                else Gui.TextUnformatted(owners[0].HierarchyItemLabel(first) ?? "[" + first.Index + "]");
                DrawNestedHierarchyReset(property, contexts, items);
                using (ImGuiScopes.Disabled(!canEditItem && !first.IsContentReference && !UsesAggregateItemDraft(first) && !IsPackageColorType(first.Type)))
                DrawAggregateCollectionValue(property, items.Cast<IHierarchyCollectionValue>().ToArray(), create =>
                    Queue((owner, _, index) => owner.PrepareNestedHierarchyUpdate(items[index], create(items[index])), "Edit item: "), () =>
                    {
                        DrawNestedHierarchyObjectType(property, contexts, items);
                        var children = items.Select((item, index) => owners[index].GetNestedHierarchyCollection(item)).ToArray();
                        if (children.All(child => child != null)) DrawAggregateHierarchyCollection(property, children.Select(child => child!).ToArray());
                        else DrawNestedHierarchyObjectFields(property, items);
                    });
                DrawNestedHierarchyReset(property, contexts, items);
                Gui.BeginDisabled(!canEditItem);
                if (contexts.Select((context, index) => owners[index].CanRemoveNestedHierarchyCollection(context)).All(value => value))
                {
                    if (Gui.SmallButton(EditorLocalization.Widget("Remove", "Button"))) Queue((owner, _, index) => owner.PrepareNestedHierarchyRemove(items[index]), "Remove item: ");
                    if (contexts.Select((context, index) => owners[index].CanReorderNestedHierarchyCollection(context)).All(value => value))
                    {
                        Gui.SameLine(); Gui.BeginDisabled(first.Index.Int == 0);
                        if (Gui.SmallButton(EditorLocalization.Widget("Move up", "Menu"))) { var target = first.Index.Int - 1; Queue((owner, _, index) => owner.PrepareNestedHierarchyMove(items[index], target), "Move item: "); }
                        Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(rows.Any(set => first.Index.Int >= set.Count - 1));
                        if (Gui.SmallButton(EditorLocalization.Widget("Move down", "Menu"))) { var target = first.Index.Int + 1; Queue((owner, _, index) => owner.PrepareNestedHierarchyMove(items[index], target), "Move item: "); }
                        Gui.EndDisabled();
                    }
                }
                Gui.EndDisabled(); Gui.PopID();
            }
            if (contexts[0].Descriptor is DictionaryDescriptor)
                DrawHierarchyDictionaryDefaultAdd(owners, contexts, property);
            else if (editable && contexts.Select((context, index) => owners[index].CanAddNestedHierarchyCollection(context)).All(value => value))
            {
                var keyText = "";
                uint? keyFeedback = null;
                DictionaryAddKeyDraft? addKeyDraft = null;
                if (contexts[0].Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.SupportsText(dictionary.KeyType))
                {
                    addKeyDraft = DrawAggregateDictionaryAddKey(property, contexts);
                    keyText = addKeyDraft.Text;
                    keyFeedback = Gui.GetID("##Key");
                }
                var defaultAdd = contexts.All(context => context.Descriptor is CollectionDescriptor)
                    && contexts.Select((context, index) => owners[index].HierarchyCollectionNativeEntries(context)).All(entries => entries == null);
                if (Gui.Button("Add"))
                {
                    if (defaultAdd) pendingCollectionAction = () => SelectHierarchyDefaultCollectionAdd(owners, contexts,
                        () => ApplyAggregateMutation(property, field =>
                        {
                            var index = AggregateFieldIndex(property, field);
                            return owners[index].PrepareHierarchyDefaultCollectionAdd(contexts[index]);
                        }, "Add item: " + property.First.Label));
                    else Execute(() => { ValidatePendingPropertyStrings(); Gui.OpenPopup("AddItem"); });
                }
                if (!defaultAdd && Gui.BeginPopup("AddItem"))
                {
                    keyText = addKeyDraft?.Text ?? keyText;
                    var declared = contexts[0].ElementType!;
                    if (ContentAssetReference.Supports(declared))
                    {
                        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
                            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
                        DrawContentReferenceValue(declared, null, false, reference => referencePhase.Submit(() =>
                            { Add(null, reference); ApplyPendingCollectionAction(); }));
                    }
                    if (SceneObjectReference.Supports(declared))
                        foreach (var part in owners[0].Asset.Hierarchy.Parts.Values)
                        {
                            if (!owners.All(owner => owner.Asset.Hierarchy.Parts.TryGetValue(part.Entity.Id, out var candidate) && ReferenceEquals(candidate.Entity, part.Entity))) continue;
                            IEnumerable<object> candidates = SceneObjectReference.AcceptsEntity(declared, part.Entity) ? [part.Entity] : part.Entity.Components.Where(declared.IsInstanceOfType);
                            foreach (var candidate in candidates) if (Gui.Selectable(SceneReferenceLabel(candidate))) Add(null, candidate);
                        }
                    var entries = NativeAbstractEntries.Combine(contexts.Select((context, index) => owners[index].HierarchyCollectionNativeEntries(context)?.ToArray() ?? []).ToArray());
                    NativeAbstractEntryControl.Choices(entries, null, entry =>
                    {
                        var text = keyText;
                        pendingCollectionAction = () => SelectHierarchyCollectionEntries(owners, contexts, entry,
                            () => ApplyAggregateMutation(property, field =>
                            {
                                var index = AggregateFieldIndex(property, field);
                                return keyFeedback is { } feedback ? PropertyConversionFeedback.Convert(feedback,
                                    () => owners[index].PrepareHierarchyCollectionEntry(contexts[index], entry, text))
                                    : owners[index].PrepareHierarchyCollectionEntry(contexts[index], entry, text);
                            }, "Add item: " + property.First.Label));
                    });
                    Gui.EndPopup();
                }
                void Add(Type? type, object? reference)
                {
                    var text = keyText;
                    Queue((owner, context, _) => owner.PrepareNestedHierarchyAdd(context, type, context.Descriptor is DictionaryDescriptor ? text : null, reference), "Add item: ");
                    if (keyFeedback is { } feedback && pendingCollectionAction is { } add)
                        pendingCollectionAction = () => PropertyConversionFeedback.Run(feedback, add);
                }
            }
            Gui.TreePop();
        }
        Gui.PopID();
        void Queue(Func<EntityHierarchyDocument, HierarchyCollectionContext, int, Action> prepare, string title)
        {
            var seen = new HashSet<IAssetObjectNode>(ReferenceEqualityComparer.Instance);
            QueueAggregateMutation(property, field =>
            {
                var index = AggregateFieldIndex(property, field); var context = contexts[index]; var owner = owners[index];
                if (!owner.CanEditNestedHierarchyCollection(context)) throw new InvalidOperationException("The nested collection owner changed.");
                var change = prepare(owner, context, index);
                return seen.Add(context.Node) ? change : () => { };
            }, title + property.First.Label);
        }
    }
}
