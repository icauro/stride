namespace Stride.Editor.ImGui;

/// <summary>Path changes preserve authored data, saved disk fingerprint and document history.</summary>
internal interface IRelocatableEditorDocument : IEditorDocument
{
    ReadOnlyMemory<byte> DiskContentHash { get; }
    void RelocatePath(string path);
}
