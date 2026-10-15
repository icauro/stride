namespace Stride.Editor.ImGui;

/// <summary>Actual authoring boundary retained by an awaited native presenter command.</summary>
internal interface IEditorCommandMutation : IDisposable
{
    void Commit(string title);
    void Rollback();
}
internal sealed class EditorAssetCommandMutation(EditorDeferredAssetMutation mutation) : IEditorCommandMutation
{
    public void Commit(string title) => mutation.Commit(title);
    public void Rollback() => mutation.Rollback();
    public void Dispose() => mutation.Dispose();
}
