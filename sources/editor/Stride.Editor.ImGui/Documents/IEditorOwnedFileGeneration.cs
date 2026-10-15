namespace Stride.Editor.ImGui;

/// <summary>Retained generations share only provenance of successful owned file writes.</summary>
internal interface IEditorOwnedFileGeneration
{
    void AcceptOwnedSaveBaseline(byte[] contentHash, bool savingGeneration);
}
