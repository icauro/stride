using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal long VerificationRenderRevision { get; private set; }
    internal Texture? VerificationRenderedTarget { get; private set; }

    // Called only after the native scene authority returns from its actual render.
    internal void RecordVerificationSceneRender(Texture target)
    {
        VerificationRenderedTarget = target;
        VerificationRenderRevision++;
    }
}

internal sealed partial class EditorGame
{
    internal long VerificationSceneRenderRevision => InspectionViewport is { } view
        && ReferenceEquals(view.Target, view.VerificationRenderedTarget) ? view.VerificationRenderRevision : 0;
}
