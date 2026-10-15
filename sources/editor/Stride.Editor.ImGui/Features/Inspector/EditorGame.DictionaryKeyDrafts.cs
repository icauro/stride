using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedDictionaryKey(EntityHierarchyDocument owner, CollectionItem item)
    {
        bool Editable() => !IsAuthoringPipelineUpdating && ReferenceEquals(document, owner)
            && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
            && item.Field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && owner.CanEditDictionaryKeys(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index);
        object?[] Read()
        {
            var values = new List<object?> { item.Index.Value, item.Value, item.Collection, item.Id, item.Index, owner.EditRevision };
            foreach (var index in item.Collection.Indices ?? []) { values.Add(index); values.Add(item.Collection.Retrieve(index)); }
            return values.ToArray();
        }
        Gui.SetNextItemWidth(-1); var feedback = Gui.GetID("##Key");
        try
        {
            PropertyGrid.Strings.Draw(item.Collection, false, Editable, Read, text =>
            {
                if (!Editable()) throw new InvalidOperationException("The dictionary key changed before validation.");
                RunHierarchyFactoryCommand([owner], AcquireHierarchyFactoryTargets(1,
                    _ => owner.AcquireHierarchyItemTarget(item.Collection, item.Index)), "RenameStringKey", text,
                    () => owner.RenameDictionaryKey(item, text));
            }, () => AcquirePropertyStringOwners([owner]), format: value => DictionaryKeyText(value!),
                controlLabel: "##Key", refreshAfterCommit: false, cancelOnLostFocus: true);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}
