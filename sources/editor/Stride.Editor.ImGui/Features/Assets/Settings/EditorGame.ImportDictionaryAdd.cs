using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void AddDefaultImportCollectionItem(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !owner.CanAddImportCollection(field)
            || sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is not { IsPublished: true } state || !ReferenceEquals(state.Document, owner))
            throw new InvalidOperationException("The collection is no longer editable.");
        var target = owner.AcquireExtensionFactoryTarget(new NodeAccessor(field.Collection!, NodeIndex.Empty)); var transferred = false;
        try
        {
            var command = target?.Presenter.Commands.FirstOrDefault(value => value.Name == "AddNewItem");
            if (command != null) { StartEditorExtensionCommand(owner, target!, command, null); transferred = true; }
            else owner.AddDefaultImportCollectionItem(field);
        }
        finally
        {
            if (!transferred && target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
    private void DrawImportDictionaryAdd(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        bool Current() => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner)
            && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state
            && ReferenceEquals(state.Document, owner) && owner.CanAddImportDictionaryKey(field);
        object?[] Read()
        {
            var result = new List<object?> { field.Collection, field.Node, field.Node.Retrieve(), field.Key,
                owner.EditRevision, owner.SessionHistory?.Extensions?.Version };
            foreach (var index in field.Collection!.Indices ?? [])
            { result.Add(index); result.Add(field.Collection.IndexToId(index)); result.Add(field.Collection.Retrieve(index)); }
            return result.ToArray();
        }
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The dictionary is no longer editable.");
                leases.Add(AcquirePropertyStringOwner(owner));
                if (owner.AcquireExtensionFactoryTarget(new NodeAccessor(field.Collection!, NodeIndex.Empty)) is { } target) leases.Add(target);
                return new PropertyStringOwners(leases, [owner]);
            }
            catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
        }
        void Add(object? parameter)
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The dictionary is no longer editable.");
            var target = owner.AcquireExtensionFactoryTarget(new NodeAccessor(field.Collection!, NodeIndex.Empty)); var transferred = false;
            try
            {
                var command = target?.Presenter.Commands.FirstOrDefault(value => value.Name == "AddPrimitiveKey");
                if (command != null)
                { StartEditorExtensionCommand(owner, target!, command, parameter); transferred = true; }
                else owner.AddDefaultImportDictionaryKey(field, parameter);
            }
            finally
            {
                if (!transferred && target != null)
                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        }
        if (field.Collection?.Descriptor is DictionaryDescriptor descriptor && owner.CanAddImportDictionaryKey(field))
            DrawNativeDictionaryAdd(field.Collection, descriptor, Current, Read, Add, Acquire);
    }
}
