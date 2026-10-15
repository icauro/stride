using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawImportDictionaryKey(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        bool Current() => CanEditImportItemDraft(owner, item) && owner.CanRenameImportCollectionKey(item);
        object?[] Read()
        {
            var values = new List<object?> { item.Index.Value };
            values.AddRange(ReadImportItemDraft(owner, item));
            foreach (var index in item.Collection.Indices ?? [])
            { values.Add(index); values.Add(item.Collection.Retrieve(index)); }
            return values.ToArray();
        }
        var feedback = Gui.GetID("Key");
        try
        {
            PropertyGrid.Strings.Draw(item.Collection, false, Current, Read, text =>
            {
                if (!Current()) throw new InvalidOperationException("The dictionary key is no longer editable.");
                var target = owner.AcquireImportItemCommandTarget(item); var transferred = false;
                try
                {
                    var command = target?.Presenter.Commands.FirstOrDefault(value => value.Name == "RenameStringKey");
                    if (command != null)
                    {
                        StartEditorExtensionCommand(owner, target!, command, text,
                            () => new EditorAssetCommandMutation(owner.BeginExtensionCommandMutation()));
                        transferred = true;
                    }
                    else owner.RenameImportCollectionKey(item, text);
                }
                finally
                {
                    if (!transferred && target != null)
                        try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
                }
            }, () => AcquireImportItemDraft(owner, item), format: value => DictionaryKeyText(value!),
                controlLabel: "Key", refreshAfterCommit: false, cancelOnLostFocus: true,
                onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
        PropertyConversionFeedback.Draw(feedback);
    }
}
