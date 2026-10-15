using Microsoft.CodeAnalysis;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    internal void AddBlock(Method method, Block block, Int2 position, Compilation? compilation)
    {
        RequireMethod(method);
        if (Asset.ContainsPart(block.Id) || block.Slots.Select(slot => slot.Id).Distinct().Count() != block.Slots.Count)
            throw new InvalidOperationException("The block or its slot identities are already occupied.");
        block.Position = position;
        if (block.Slots.Count == 0)
        {
            var slots = new List<Slot>(); block.GenerateSlots(slots, new SlotGeneratorContext(compilation));
            foreach (var slot in slots) block.Slots.Add(slot);
        }
        var node = CollectionNode(method, nameof(Method.Blocks));
        MutateCollection(node, () => node.Add(block, new NodeIndex(block.Id)), "Add block");
    }
    internal bool CanConnect(Method method, Slot first, Slot second)
        => Asset.Methods.Contains(method) && first != second && first.Kind == second.Kind && first.Direction != second.Direction
        && first.Owner != null && second.Owner != null && method.Blocks.Values.Contains(first.Owner) && method.Blocks.Values.Contains(second.Owner);
    internal void Connect(Method method, Slot first, Slot second)
    {
        RequireMethod(method);
        if (!CanConnect(method, first, second)) throw new InvalidOperationException("The slots cannot be connected.");
        var source = first.Direction == SlotDirection.Output ? first : second;
        var target = first.Direction == SlotDirection.Input ? first : second;
        if (method.Links.Values.Any(link => ReferenceEquals(link.Source, source) && ReferenceEquals(link.Target, target))) return;
        var node = CollectionNode(method, nameof(Method.Links));
        MutateCollection(node, () =>
        {
            foreach (var previous in method.Links.Values.Where(link => ReferenceEquals(link.Target, target)
                || source.Kind == SlotKind.Execution && ReferenceEquals(link.Source, source)).ToArray()) RemoveLinkCore(node, previous);
            var link = new Link(source, target);
            node.Add(link, new NodeIndex(link.Id));
        }, "Connect slots");
    }
    internal void Disconnect(Method method, Slot slot)
    {
        RequireMethod(method); var node = CollectionNode(method, nameof(Method.Links));
        var links = method.Links.Values.Where(link => ReferenceEquals(link.Source, slot) || ReferenceEquals(link.Target, slot)).ToArray();
        if (links.Length == 0) return;
        MutateCollection(node, () => { foreach (var link in links) RemoveLinkCore(node, link); }, "Disconnect slots");
    }
    internal void DeleteGraphSelection(Method method, IReadOnlyCollection<Guid> blocks, IReadOnlyCollection<Guid> links)
    {
        RequireMethod(method);
        var blockValues = method.Blocks.Values.Where(block => blocks.Contains(block.Id)).ToArray();
        var linkValues = method.Links.Values.Where(link => links.Contains(link.Id)
            || blockValues.Contains(link.Source?.Owner) || blockValues.Contains(link.Target?.Owner)).ToArray();
        if (blockValues.Length == 0 && linkValues.Length == 0) return;
        var blockNode = CollectionNode(method, nameof(Method.Blocks)); var linkNode = CollectionNode(method, nameof(Method.Links));
        if (!CanEditAssetSettingNode(linkNode)) throw new InvalidOperationException("The function links are not editable.");
        MutateCollection(blockNode, () =>
        {
            foreach (var link in linkValues) RemoveLinkCore(linkNode, link);
            foreach (var block in blockValues)
            {
                var index = new NodeIndex(block.Id);
                if (!CanEditAssetSettingItem(blockNode, index)) throw new InvalidOperationException("The block is inherited and sealed.");
                blockNode.Remove(block, index);
            }
        }, "Deleting selected graph elements");
    }
    private void RemoveLinkCore(IAssetObjectNode node, Link link)
    {
        var index = new NodeIndex(link.Id);
        if (!CanEditAssetSettingItem(node, index)) throw new InvalidOperationException("The link is inherited and sealed.");
        node.Remove(link, index);
    }
    internal void MoveBlocks(Method method, IReadOnlyDictionary<Guid, Int2> positions)
    {
        RequireMethod(method);
        var changes = positions.Select(pair => (Block: method.Blocks.Values.FirstOrDefault(block => block.Id == pair.Key), pair.Value))
            .Where(pair => pair.Block != null && pair.Block.Position != pair.Value).ToArray();
        if (changes.Length == 0) return;
        if (changes.Any(pair => !CanEditMember(pair.Block!, nameof(Block.Position)))) throw new InvalidOperationException("A selected block position is not editable.");
        ApplyVisualMutation(() => { foreach (var pair in changes) ObjectNode(pair.Block!)[nameof(Block.Position)].Update(pair.Value); }, "Moved graph elements");
    }
    internal void ApplyGeneratedSlots(Method method, Block block, IReadOnlyList<Slot> generated, bool withinMutation = false)
    {
        RequireMethod(method);
        if (!method.Blocks.Values.Contains(block)) throw new InvalidOperationException("The block has another function owner.");
        var previous = block.Slots.ToArray(); var used = new HashSet<Slot>(); var replacements = new List<Slot>();
        var mapping = previous.ToDictionary(slot => slot, _ => (Slot?)null);
        foreach (var suggested in generated)
        {
            var old = previous.FirstOrDefault(slot => !used.Contains(slot) && slot.Direction == suggested.Direction && slot.Kind == suggested.Kind && slot.Name == suggested.Name);
            var next = suggested;
            if (old != null)
            {
                used.Add(old); suggested.Id = old.Id; suggested.Value = old.Value ?? suggested.Value; suggested.Type ??= old.Type;
                if (suggested.Flags == old.Flags && suggested.Type == old.Type && suggested.Value == old.Value) next = old;
                mapping[old] = next;
            }
            replacements.Add(next);
        }
        if (previous.Length == replacements.Count && previous.Zip(replacements).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        if (replacements.Select(slot => slot.Id).Distinct().Count() != replacements.Count) throw new InvalidOperationException("The block generated duplicate slot identities.");
        var slots = CollectionNode(block, nameof(Block.Slots)); var links = CollectionNode(method, nameof(Method.Links));
        if (!CanEditAssetSettingNode(links)) throw new InvalidOperationException("The function links are not editable.");
        var related = method.Links.Values.Where(link => link.Source != null && mapping.ContainsKey(link.Source) || link.Target != null && mapping.ContainsKey(link.Target)).ToArray();
        var updated = new List<Link>();
        foreach (var link in related)
        {
            var source = link.Source != null && mapping.TryGetValue(link.Source, out var newSource) ? newSource : link.Source;
            var target = link.Target != null && mapping.TryGetValue(link.Target, out var newTarget) ? newTarget : link.Target;
            if (source == null || target == null) continue;
            var copy = AssetCloner.Clone(link)!; copy.Source = source; copy.Target = target; updated.Add(copy);
        }
        void ApplySlots()
        {
            foreach (var link in related) RemoveLinkCore(links, link);
            for (var index = block.Slots.Count - 1; index >= 0; index--) slots.Remove(block.Slots[index], new NodeIndex(index));
            foreach (var slot in replacements) slots.Add(slot);
            foreach (var link in updated) links.Add(link);
        }
        if (withinMutation)
        {
            if (!CanEditAssetSettingNode(slots)) throw new InvalidOperationException("The block slots are not editable.");
            ApplySlots();
        }
        else MutateCollection(slots, ApplySlots, "Updated slots");
    }
}
