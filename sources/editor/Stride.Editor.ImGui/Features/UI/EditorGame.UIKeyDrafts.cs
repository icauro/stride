using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIDictionaryKey(UIDocument source, IReadOnlyList<Guid> elementIds,
        IReadOnlyList<UIPropertyCollectionItem> selectedItems, Func<bool> canEditOwner)
    {
        var ids = elementIds.ToArray();
        var items = selectedItems.DistinctBy(item => (item.Owner.Collection, item.Identity)).ToArray();
        if (items.Length == 0) return;
        var providerVersion = source.SessionHistory?.Extensions?.Version;
        bool Current() => ReferenceEquals(ownedUIEditor?.Document, source) && ownedUIEditor.Selection.SequenceEqual(ids)
            && providerVersion == source.SessionHistory?.Extensions?.Version && canEditOwner()
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && items.All(source.CanRenameCollectionKey);
        object?[] Read()
        {
            var values = items.Select(item => item.Index.Value).ToList();
            values.Add(source.EditRevision); values.Add(source.LayoutVersion); values.Add(providerVersion);
            foreach (var item in items)
            {
                var collection = item.Owner;
                values.Add(collection.ElementId); values.Add(source.Find(collection.ElementId));
                values.Add(collection.Member); values.Add(collection.Collection); values.Add(collection.AttachedKey);
                values.Add(collection.Accessor.RetrieveValue()); values.Add(item.Id); values.Add(item.Index); values.Add(item.Value);
                foreach (var sibling in collection.Items) { values.Add(sibling.Id); values.Add(sibling.Index); values.Add(sibling.Value); }
            }
            return values.ToArray();
        }
        IDisposable Acquire()
        {
            if (!Current()) throw new InvalidOperationException("The native UI key owner changed.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
                leases.Add(source.AcquireUICollectionValueTarget(items));
                return new PropertyStringOwners(leases, [source]);
            }
            catch
            {
                foreach (var lease in leases.AsEnumerable().Reverse())
                    try { lease.Dispose(); } catch { packageCommandCleanup.Add(lease.Dispose); }
                throw;
            }
        }
        void Commit(string text)
        {
            ValidatePendingPropertyStrings(); source.CommitEdits();
            if (!Current()) throw new InvalidOperationException("The native UI key changed before validation.");
            var owners = AcquireHierarchyFactoryTargets(items.Length,
                index => source.AcquireExtensionFactoryTarget(new NodeAccessor(items[index].Owner.Collection, items[index].Index)));
            EditorPresenterTarget? combined = null; var transferred = false;
            try
            {
                if (owners.All(owner => owner != null))
                {
                    combined = new(owners.Select(owner => owner!).ToArray());
                    var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == "RenameStringKey");
                    if (command != null)
                    {
                        if (combined.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native dictionary key is read-only.");
                        StartEditorExtensionCommand(source, combined, command, text); transferred = true; return;
                    }
                }
                else if (owners.Where(owner => owner != null).SelectMany(owner => owner!.Presenters)
                    .Any(presenter => presenter.Commands.Any(command => command.Name == "RenameStringKey")))
                    throw new InvalidOperationException("A selected native key presenter is no longer available.");
                source.RenameUICollectionKeys(items, text);
            }
            finally
            {
                if (!transferred)
                {
                    if (combined != null) Release(combined);
                    else foreach (var owner in owners.OfType<EditorPresenterTarget>()) Release(owner);
                }
            }
        }
        var feedback = Gui.GetID("##Key"); Gui.SetNextItemWidth(-1);
        try
        {
            PropertyGrid.Strings.Draw(items[0].Owner.Collection, items.Skip(1).Any(item => item.Index != items[0].Index),
                Current, Read, Commit, Acquire, format: value => DictionaryKeyText(value!), controlLabel: "##Key",
                refreshAfterCommit: false, cancelOnLostFocus: true);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
        void Release(EditorPresenterTarget owner)
        {
            try { owner.Dispose(); } catch { packageCommandCleanup.Add(owner.Dispose); throw; }
        }
    }
}
