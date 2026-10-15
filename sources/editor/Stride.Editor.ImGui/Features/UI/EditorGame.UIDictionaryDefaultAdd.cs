using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawUIDictionaryDefaultAdd(UIDocument source, IReadOnlyList<UIPropertyCollection> actualCollections, Func<bool> canEditOwner)
    {
        var collections = actualCollections.ToArray();
        if (collections.Length == 0 || collections[0].Collection.Descriptor is not DictionaryDescriptor descriptor) return;
        var selection = collections.Select(collection => collection.ElementId).Distinct().ToArray();
        bool Current() => ReferenceEquals(ownedUIEditor?.Document, source) && ownedUIEditor.Selection.SequenceEqual(selection)
            && canEditOwner() && collections.All(source.CanAddUIDictionaryKey);
        object?[] Certificates()
        {
            var result = new List<object?> { source.EditRevision, source.LayoutVersion, source.SessionHistory?.Extensions?.Version };
            foreach (var collection in collections)
            {
                result.Add(collection.ElementId); result.Add(collection.Member); result.Add(collection.Collection);
                result.Add(collection.AttachedKey); result.Add(collection.Accessor.RetrieveValue());
                foreach (var item in collection.Items) { result.Add(item.Id); result.Add(item.Index); result.Add(item.Value); }
            }
            return result.ToArray();
        }
        IDisposable Acquire()
        {
            if (!Current()) throw new InvalidOperationException("The native UI dictionary owner changed.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
                if (source.AcquireUICollectionAddTarget(collections) is { } target) leases.Add(target);
                return new PropertyStringOwners(leases, [source]);
            }
            catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
        }
        void Add(object? parameter)
        {
            ValidatePendingPropertyStrings(); source.CommitEdits();
            if (!Current()) throw new InvalidOperationException("The native UI dictionary command changed before adding.");
            var target = source.AcquireUICollectionAddTarget(collections); var transferred = false;
            try
            {
                var command = target == null ? null : NodePresenterCombination.Commands(target.Presenters)
                    .FirstOrDefault(command => command.Name == "AddPrimitiveKey");
                if (command != null)
                {
                    if (target!.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native dictionary is read-only.");
                    StartEditorExtensionCommand(source, target!, command, parameter); transferred = true; return;
                }
                source.AddUIDictionaryDefaultItems(collections, parameter);
            }
            finally
            {
                if (!transferred && target != null)
                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        }
        DrawNativeDictionaryAdd(collections[0].Collection, descriptor, Current, Certificates, Add, Acquire);
    }
}
