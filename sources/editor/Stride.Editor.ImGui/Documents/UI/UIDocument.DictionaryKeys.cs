using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal void RenameUICollectionKeys(IReadOnlyList<UIPropertyCollectionItem> items, string text)
    {
        EndEdit();
        var changes = items.DistinctBy(item => (item.Owner.Collection, item.Identity)).Select(Prepare).OfType<Action>().ToArray();
        if (changes.Length == 0) return;
        BatchCollectionEdits(() =>
        {
            foreach (var change in changes) Mutate(change, "Rename dictionary key");
        }, "Rename dictionary key");
        Action? Prepare(UIPropertyCollectionItem item)
        {
            if (!CanRenameCollectionKey(item)) throw new InvalidOperationException("The dictionary key cannot be renamed.");
            var collection = item.Owner.Collection; var descriptor = (DictionaryDescriptor)collection.Descriptor;
            var key = NativeDictionaryKey.Parse(descriptor.KeyType, text); var target = new NodeIndex(key);
            var before = item.Index;
            if (target == before || descriptor.ContainsKey(collection.Retrieve(), key)) return null;
            var id = item.Id!.Value; var value = item.Value;
            var itemOverride = collection.GetItemOverride(before); var keyOverride = collection.GetKeyOverride(before);
            return () =>
            {
                if (!CanRenameCollectionKey(item) || item.Index != before || !Equals(item.Value, value))
                    throw new InvalidOperationException("The native UI dictionary item changed before renaming.");
                if (descriptor.ContainsKey(collection.Retrieve(), key)) return;
                collection.RemoveAndDiscard(value, collection.IdToIndex(id), id);
                collection.Restore(value, target, id);
                collection.RestoreItemOverrides(target, itemOverride, keyOverride);
            };
        }
    }
}
