using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanExecuteNativeFactory(AssetMetadataDocument owner, NodeAccessor accessor, IReadOnlyList<AbstractNodeEntry> entries)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner)) return false;
        var target = owner.AcquireExtensionFactoryTarget(accessor);
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
