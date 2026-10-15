using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralDictionaryKey(ProceduralAssetDocument asset, ProceduralCollectionItem item)
    {
        bool Current() => !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset) && asset.CanRenameCollectionKey(item);
        object?[] Read()
        {
            var values = new List<object?> { item.Index.Value, item.Value, item.Context.Node, item.Index, item.Id, asset.EditRevision };
            foreach (var index in item.Context.Node.Indices ?? []) { values.Add(index); values.Add(item.Context.Node.Retrieve(index)); }
            for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
            { values.Add(parent.Context.Node); values.Add(parent.Index); values.Add(parent.Key); values.Add(parent.Id); }
            return values.ToArray();
        }
        var feedback = Gui.GetID("##Key");
        try
        {
            PropertyGrid.Strings.Draw(item.Context.Node, false, Current, Read, text =>
            {
                var target = asset.AcquireCollectionItemTarget(item); var transferred = false;
                try
                {
                    var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "RenameStringKey");
                    if (command != null)
                    {
                        StartEditorExtensionCommand(asset, target!, command, text,
                            () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                        transferred = true;
                    }
                    else asset.RenameCollectionKey(item, item.Index.Value, text);
                }
                finally
                {
                    if (!transferred && target != null)
                        try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
                }
            }, () => AcquireRetainedProceduralNumber(asset, () => asset.AcquireCollectionItemTarget(item)),
                format: value => DictionaryKeyText(value!), controlLabel: "##Key", refreshAfterCommit: false, cancelOnLostFocus: true,
                onCancelled: error => ClearCancelledPropertyDraft(error, feedback, procedural: true));
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error, procedural: true); }
        PropertyConversionFeedback.Draw(feedback);
    }
}
