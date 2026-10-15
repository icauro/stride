using Hexa.NET.ImGui;
using Stride.Core.Assets.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool HasPendingPropertyStrings => propertyGridPanel?.Strings.HasPending == true || HasPendingPackagePropertyStrings || spriteSourceEdits.Count != 0 || spriteWholeImages != null;
    private void ValidatePendingPropertyStrings()
    {
        propertyGridPanel?.Strings.ValidatePending();
        ValidatePendingPackagePropertyStrings();
        ValidateSpriteSourceAssignments();
        if (IsEditorExtensionCommandRunning)
            throw new InvalidOperationException("A native editor command must finish before continuing.");
    }
    private void AbandonPendingPropertyStrings()
    {
        propertyGridPanel?.Strings.AbandonPending();
        AbandonPendingPackagePropertyStrings();
        ClearSpriteSourceEdits();
    }
    private void AbandonPendingPropertyStrings(IEditorDocument owner)
    {
        propertyGridPanel?.Strings.AbandonPending(owner);
        AbandonPendingPackagePropertyStrings(owner);
        if (owner is AssetMetadataDocument spriteOwner) ReleaseSpriteSourceEdit(spriteOwner);
    }
    private sealed class PropertyStringOwners(List<IDisposable> leases, IReadOnlyList<IEditorDocument> owners) : IPropertyDraftOwnerLease
    {
        public bool ContainsOwner(IEditorDocument owner) => owners.Any(candidate => ReferenceEquals(candidate, owner));
        public void Dispose()
        {
            while (leases.Count != 0)
            {
                var index = leases.Count - 1;
                leases[index].Dispose();
                leases.RemoveAt(index);
            }
        }
    }
    private IDisposable AcquirePropertyStringOwners(IEnumerable<EntityHierarchyDocument> owners)
    {
        var ownedDocuments = owners.Distinct().ToArray();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var owner in ownedDocuments)
            {
                var state = owner.SessionHistory?.Registry.Find(owner.Asset.Id);
                if (state is not { IsPublished: true } || !ReferenceEquals(state.Document, owner))
                    throw new InvalidOperationException("The text property owner is no longer current.");
                leases.Add(state.AcquireView());
            }
            return new PropertyStringOwners(leases, ownedDocuments);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(leases, ownedDocuments);
            try { cleanup.Dispose(); }
            catch (Exception release)
            {
                packageCommandCleanup.Add(cleanup.Dispose);
                throw new AggregateException(failure, release);
            }
            throw;
        }
    }
    private IDisposable AcquirePropertyStringOwner(IAssetTagDocument owner)
    {
        var state = sessionHistory?.Registry.Find(owner.AuthoredAsset.Id);
        if (state is not { IsPublished: true } || !ReferenceEquals(state.Document, owner))
            throw new InvalidOperationException("The text property owner is no longer current.");
        return new PropertyStringOwners([state.AcquireView()], [owner]);
    }
    private void DrawComponentString(ComponentField field)
    {
        var owner = document!;
        Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                PropertyGrid.Strings.Draw(field.Node, false,
                    () => ReferenceEquals(document, owner) && field.SupportsEditing && owner.CanEditComponentValue(field)
                        && field.Component.Entity is { } entity && owner.IsSelected(entity.Id),
                    () => [field.Node.Retrieve()], replacement => { owner.UpdateComponent(field, replacement); owner.EndComponentEdit(); },
                    () => AcquirePropertyStringOwners([owner]));
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
            }
            catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void DrawInheritedString(ComponentField field)
    {
        var owner = document!;
        try
        {
            PropertyGrid.Strings.Draw(field.Node, false,
            () => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating && owner.CanOverrideInheritedComponent(field)
                && field.Component.Entity is { } entity && owner.IsSelected(entity.Id),
            () => [field.Node.Retrieve()], replacement => { owner.EditInheritedScalar(field, replacement); componentErrors.Remove(field); },
            () => AcquirePropertyStringOwners([owner]));
        }
        catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
    }
    private void DrawCommonString(CommonComponentProperty property, bool inherited)
    {
        var owner = document!;
        PropertyGrid.Strings.Draw(property.First.Node, property.IsMixed,
            () => ReferenceEquals(document, owner) && owner.SelectedCount == property.Fields.Count
                && (inherited ? CanEditInheritedCommon(property) : owner.CanEditCommonProperty(property)),
            () => property.Fields.Select(field => field.Node.Retrieve()).ToArray(), replacement =>
            {
                if (inherited) owner.EditInheritedScalars(property.Fields, property.Fields.Select(_ => (object?)replacement).ToArray());
                else { owner.UpdateCommonProperty(property, replacement); owner.EndCommonPropertyEdit(); }
            }, () => AcquirePropertyStringOwners([owner]));
    }
    private void DrawAggregateString(CommonComponentProperty property)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        PropertyGrid.Strings.Draw(property.First.Node, property.IsMixed, () => CanEditAggregateProperty(property),
            () => property.Fields.Select(field => field.Node.Retrieve()).ToArray(),
            replacement => ApplyAggregateProperty(property, _ => replacement), () => AcquirePropertyStringOwners(owners));
    }
    private void DrawCollectionString(CollectionItem item)
    {
        var owner = document!;
        PropertyGrid.Strings.Draw(item.Collection, false,
            () => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating && item.Collection.HasId(item.Id)
                && ReferenceEquals(item.Field.Node.Target, item.Collection)
                && item.Field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
                && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
            () => [item.Value, item.Index, item.Id], replacement => { owner.UpdateCollectionItem(item, replacement); owner.EndCollectionEdit(); },
            () => AcquirePropertyStringOwners([owner]));
    }
    private void DrawAggregateCollectionString(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> items)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        PropertyGrid.Strings.Draw(items[0].Collection, items.Skip(1).Any(item => !Equals(items[0].Value, item.Value)),
            () => CanEditAggregateCollection(property) && items.Select((item, index) => aggregatePropertyOwners[property.Fields[index]].CanEditHierarchyItem(item.Collection, item.Index)).All(editable => editable),
            () => items.Select(item => item.Value).ToArray(), replacement =>
                ApplyAggregateMutation(property, field =>
                {
                    var item = items[AggregateFieldIndex(property, field)];
                    var owner = aggregatePropertyOwners[field];
                    return item switch
                    {
                        CollectionItem regular => owner.PrepareAggregateCollectionUpdate(regular, replacement),
                        HierarchyCollectionItem nested => owner.PrepareNestedHierarchyUpdate(nested, replacement),
                        _ => throw new InvalidOperationException("The text collection item is no longer editable.")
                    };
                }, "Edit item: " + property.First.Label), () => AcquirePropertyStringOwners(owners));
    }
}
