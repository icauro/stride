using Stride.Assets.Presentation.AssetEditors.GameEditor;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EntityHierarchyDocument, SceneEditorTransientState> sceneEditorTransientStates = new();
    private sealed class SceneEditorTransientState
    {
        internal WeakReference<EditorDocumentEntry>? View;
        internal bool MaterialSelection;
        internal int Transformation;
        internal TransformationSpace Space = TransformationSpace.WorldSpace;
    }
    private SceneEditorTransientState? CurrentSceneEditorTransientState()
    {
        if (engineViewport?.PresentedDocument is not { } owner || documents.Find(owner) is not { } view) return null;
        var state = sceneEditorTransientStates.GetValue(owner, _ => new SceneEditorTransientState());
        // Canonical history may retain the document after its logical editor
        // closes. A later new editor still starts with the native defaults.
        if (state.View == null || !state.View.TryGetTarget(out var previous) || !ReferenceEquals(previous, view))
        {
            state.View = new(view); state.MaterialSelection = false;
            state.Transformation = 0; state.Space = TransformationSpace.WorldSpace;
        }
        return state;
    }
}
