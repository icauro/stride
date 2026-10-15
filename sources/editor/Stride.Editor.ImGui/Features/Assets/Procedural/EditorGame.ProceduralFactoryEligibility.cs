using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanExecuteProceduralFactory(ProceduralAssetDocument owner, AssetPropertyField field,
        IReadOnlyList<AbstractNodeEntry> entries)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentProceduralControlOwner(owner) || !owner.IsPropertyFieldCurrent(field)) return false;
        var target = owner.AcquireExtensionCommandTarget(field.Node);
        if (target == null) return false;
        try
        {
            if (target.Presenters.Any(presenter => presenter.IsReadOnly)) return false;
            var command = target.Presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
            return command != null && entries.Any(entry => command.CanExecute(target.Presenters, entry));
        }
        finally
        {
            try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
