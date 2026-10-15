using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralDictionaryAdd(ProceduralAssetDocument asset, ProceduralCollectionContext context)
    {
        if (context.Node.Descriptor is not DictionaryDescriptor descriptor) return;
        bool Current() => !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset) && asset.CanAddCollection(context);
        object?[] Certificates()
        {
            var values = new List<object?> { context.Node, asset.EditRevision };
            foreach (var index in context.Node.Indices ?? []) { values.Add(index); values.Add(context.Node.Retrieve(index)); }
            for (var parent = context.Parent; parent != null; parent = parent.Context.Parent)
            { values.Add(parent.Context.Node); values.Add(parent.Index); values.Add(parent.Key); values.Add(parent.Id); }
            return values.ToArray();
        }
        DrawNativeDictionaryAdd(context.Node, descriptor, Current, Certificates, parameter =>
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The procedural dictionary changed before adding the key.");
            var target = asset.AcquireCollectionTarget(context); var transferred = false;
            try
            {
                var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "AddPrimitiveKey");
                if (command != null)
                {
                    StartEditorExtensionCommand(asset, target!, command, parameter,
                        () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                    transferred = true;
                }
                else asset.AddDefaultDictionaryItem(context, parameter);
            }
            finally
            {
                if (!transferred && target != null)
                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        }, () => AcquireRetainedProceduralNumber(asset, () => asset.AcquireCollectionTarget(context)));
    }
}
