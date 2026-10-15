namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument : IEditorOwnedFileGeneration
{
    void IEditorOwnedFileGeneration.AcceptOwnedSaveBaseline(byte[] hash, bool savingGeneration)
    {
        diskHash = hash.ToArray();
        if (!savingGeneration) { savedRevision = -1; savedText = null; }
    }
}
