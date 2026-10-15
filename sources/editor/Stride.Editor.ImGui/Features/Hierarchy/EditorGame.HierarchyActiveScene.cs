namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SetActiveHierarchyScene(SceneDocument? selected)
    {
        // Native SetActiveRoot does nothing when the last selected content is not a root.
        if (selected == null || ReferenceEquals(document, selected)) return;
        if (!ReferenceEquals(SelectionService.LastHierarchySelectedContent, selected)
            || !IsCurrentHierarchyContextOwner(selected))
            throw new InvalidOperationException("The selected active scene changed.");
        ActivateSceneRoot(selected, allowUnloaded: true);
    }
}
